using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using LibVLCSharp.Shared;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace AoIP_RX.Services;

public enum EngineState { Stopped, Connecting, Buffering, Playing, Reconnecting }

/// <summary>
/// Decode with LibVLC (mms + shoutcast/icecast), PCM tap for accurate L/R
/// meters + auto-balance DSP, output via NAudio WASAPI. Portable.
/// </summary>
public sealed class StreamEngine : IDisposable
{
    private readonly Logger _log;
    private LibVLC? _lib;
    private MediaPlayer? _player;
    private WasapiOut? _out;
    private BufferedWaveProvider? _tap;
    private MMDevice? _device;

    private MediaPlayer.LibVLCAudioPlayCb? _playCb;
    private MediaPlayer.LibVLCAudioPauseCb? _pauseCb;
    private MediaPlayer.LibVLCAudioResumeCb? _resumeCb;
    private MediaPlayer.LibVLCAudioFlushCb? _flushCb;
    private MediaPlayer.LibVLCAudioDrainCb? _drainCb;

    private CancellationTokenSource? _cts;
    private Task? _loop;
    private volatile bool _userStop;
    private string _url = "";
    private int _rate = 48000;
    private int _cache = 300;
    private long _pcmTicks;
    private int _attempt;

    // meters (linear 0..1 peak, written by audio thread, read by UI)
    private volatile float _peakL, _peakR;
    private float _showL, _showR;

    // auto-balance: slow gain trim on the weaker channel
    private double _gainDbL, _gainDbR;      // applied correction, clamped ±6
    private double _sumSqL, _sumSqR;
    private long _sumN;
    private DateTime _windowStart = DateTime.UtcNow;
    private DateTime _imbalancedSince;
    private bool _imbalanceLogged;
    private bool _autoBalance = true;

    public event Action? StateChanged;
    public event Action? LevelsChanged;

    public EngineState State { get; private set; } = EngineState.Stopped;
    public string StatusText { get; private set; } = "Ready";
    public string DetailText { get; private set; } = "";
    public int Attempt => _attempt;
    public float LevelL => _showL;
    public float LevelR => _showR;
    public double BalanceDbL => _gainDbL;
    public double BalanceDbR => _gainDbR;

    public StreamEngine(Logger log) => _log = log;

    public bool AutoBalance
    {
        get => _autoBalance;
        set { _autoBalance = value; if (!value) { _gainDbL = _gainDbR = 0; } }
    }

    public void Configure(int sampleRate, bool fastMode)
    {
        _rate = sampleRate is 44100 or 48000 or 96000 ? sampleRate : 48000;
        _cache = fastMode ? 300 : 1000;
    }

    // ---------- devices ----------

    public static List<(string Id, string Name, bool Default)> ListDevices()
    {
        var list = new List<(string, string, bool)>();
        try
        {
            using var e = new MMDeviceEnumerator();
            var def = e.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia)?.ID;
            foreach (var d in e.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active))
            {
                try { list.Add((d.ID, d.FriendlyName, d.ID == def)); d.Dispose(); }
                catch { }
            }
        }
        catch { }
        return list;
    }

    // ---------- transport ----------

    public void Play(string url)
    {
        StopInternal(user: false);
        _userStop = false;
        _url = url.Trim();
        _attempt = 0;
        _gainDbL = _gainDbR = 0;
        _sumSqL = _sumSqR = 0; _sumN = 0;
        _imbalancedSince = DateTime.UtcNow;
        _imbalanceLogged = false;
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;
        _loop = Task.Run(() => RunLoop(ct));
        _loop.ContinueWith(t =>
        {
            if (t.IsFaulted)
                _log.Error("Engine crashed: " + (t.Exception?.GetBaseException().Message ?? "?"));
        });
    }

    public void Stop()
    {
        _userStop = true;
        StopInternal(user: true);
        SetState(EngineState.Stopped, "Stopped", "");
    }

    private void StopInternal(bool user)
    {
        try { _cts?.Cancel(); } catch { }
        _cts = null;
        try { _player?.Stop(); } catch { }
        try { _out?.Stop(); } catch { }
        CleanupAudio();
        try { _player?.Dispose(); } catch { }
        _player = null;
        if (user) { _showL = _showR = 0; LevelsChanged?.Invoke(); }
    }

    private void CleanupAudio()
    {
        try { _out?.Dispose(); } catch { }
        _out = null;
        _tap = null;
        try { _device?.Dispose(); } catch { }
        _device = null;
    }

    private async Task RunLoop(CancellationToken ct)
    {
        EnsureLib();
        var current = _url;
        while (!ct.IsCancellationRequested && !_userStop)
        {
            _attempt++;
            // MMS fallback: 2nd attempt tries http:// variant (MMSH-over-HTTP)
            if (_attempt == 2 && current.StartsWith("mms://", StringComparison.OrdinalIgnoreCase))
            {
                current = "http://" + current[6..];
                _log.Warn($"MMS retry via fallback {current}");
            }
            try
            {
                var ok = await TryOnce(current, ct);
                if (ok) return; // played until user stopped
            }
            catch (OperationCanceledException) { return; }
            catch (Exception ex) { _log.Warn($"Attempt #{_attempt} error: {ex.Message}"); }

            if (_userStop || ct.IsCancellationRequested) return;
            SetState(EngineState.Reconnecting, $"Reconnecting #{_attempt} in 4s…", DetailText);
            _log.Warn($"Drop detected, retry #{_attempt + 1} in 4s…");
            try { await Task.Delay(4000, ct); } catch { return; }
        }
    }

    private async Task<bool> TryOnce(string url, CancellationToken ct)
    {
        SetState(_attempt > 1 ? EngineState.Reconnecting : EngineState.Connecting,
            _attempt > 1 ? $"Reconnecting #{_attempt}…" : "Connecting…", "");
        _log.Info((_attempt > 1 ? $"Retry #{_attempt}: " : "Connecting: ") + url);

        CleanupAudio();
        try { _player?.Dispose(); } catch { }

        _player = new MediaPlayer(_lib!);
        HookPlayer(_player);
        _player.SetAudioFormat("S16N", (uint)_rate, 2);
        _playCb = OnAudioPlay; _pauseCb = (_, _) => { }; _resumeCb = (_, _) => { };
        _flushCb = (_, _) => { }; _drainCb = _ => { };
        _player.SetAudioCallbacks(_playCb, _pauseCb, _resumeCb, _flushCb, _drainCb);

        using var media = new Media(_lib!, url, FromType.FromLocation);
        media.AddOption(":network-caching=" + _cache);
        _player.Media = media;

        _pcmTicks = 0;
        var played = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        void OnDone() { try { played.TrySetResult(true); } catch { } }
        _player.EndReached += (_, _) => { if (!_userStop) OnDone(); };
        _player.EncounteredError += (_, _) => OnDone();

        if (!_player.Play()) { _log.Error("Player refused to start"); return false; }

        // wait for first audio (or error/end) up to 20s
        var okStart = await WaitForAudioOrDone(played.Task, TimeSpan.FromSeconds(20), ct);
        if (!okStart) { _log.Warn("No audio received (timeout)"); return false; }

        SetState(EngineState.Playing, "Playing", "");
        RefreshTrackInfo();
        _log.Info("Playing: " + DetailText);

        // stall watchdog: PCM gap > 5s while supposed to play => reconnect
        while (!ct.IsCancellationRequested && !_userStop)
        {
            var done = await Task.WhenAny(played.Task, Task.Delay(1000, ct));
            if (done == played.Task) return _userStop; // ended/error: false => reconnect
            var gap = DateTime.UtcNow - new DateTime(Interlocked.Read(ref _pcmTicks), DateTimeKind.Utc);
            if (_player?.IsPlaying == true && gap > TimeSpan.FromSeconds(5))
            {
                _log.Warn("Stall: no audio for 5s");
                return false;
            }
            PollMeta();
        }
        return true;
    }

    private async Task<bool> WaitForAudioOrDone(Task done, TimeSpan timeout, CancellationToken ct)
    {
        var sw = DateTime.UtcNow;
        while (DateTime.UtcNow - sw < timeout)
        {
            if (ct.IsCancellationRequested || _userStop) return false;
            if (done.IsCompleted) return false;
            if (Interlocked.Read(ref _pcmTicks) != 0) return true;
            try { await Task.Delay(250, ct); } catch { return false; }
        }
        return Interlocked.Read(ref _pcmTicks) != 0 && !done.IsCompleted;
    }

    // ---------- audio path: libvlc decode -> DSP -> WASAPI ----------

    private void EnsureOutput()
    {
        if (_out != null) return;
        _device = PickDevice();
        var fmt = new WaveFormat(_rate, 16, 2);
        _tap = new BufferedWaveProvider(fmt)
        {
            BufferDuration = TimeSpan.FromSeconds(5),
            DiscardOnBufferOverflow = true
        };
        _out = new WasapiOut(_device, AudioClientShareMode.Shared, false, 100);
        _out.Init(_tap);
        _out.Volume = 1.0f;
        _out.Play();
        _log.Info($"Output: {_device.FriendlyName} @ {_rate}Hz");
    }

    private MMDevice PickDevice()
    {
        using var e = new MMDeviceEnumerator();
        var want = App.Current is App app ? app.Store.Settings.OutputDeviceId : null;
        if (!string.IsNullOrEmpty(want))
        {
            try { return e.GetDevice(want); }
            catch { _log.Warn("Saved output missing, using default"); }
        }
        return e.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
    }

    private void OnAudioPlay(IntPtr data, IntPtr samples, uint count, long pts)
    {
        try
        {
            EnsureOutput();
            int bytes = checked((int)count * 4); // S16N stereo
            if (bytes <= 0 || bytes > 1 << 20) return;
            var buf = new byte[bytes];
            Marshal.Copy(samples, buf, 0, bytes);
            ProcessPcm16(buf);
            _tap?.AddSamples(buf, 0, bytes);
            Interlocked.Exchange(ref _pcmTicks, DateTime.UtcNow.Ticks);
        }
        catch { }
    }

    private void ProcessPcm16(byte[] buf)
    {
        float peakL = 0, peakR = 0;
        double gL = Math.Pow(10, _gainDbL / 20), gR = Math.Pow(10, _gainDbR / 20);
        for (int i = 0; i + 3 < buf.Length; i += 4)
        {
            short sL = (short)(buf[i] | (buf[i + 1] << 8));
            short sR = (short)(buf[i + 2] | (buf[i + 3] << 8));
            double dL = sL / 32768.0, dR = sR / 32768.0;
            _sumSqL += dL * dL; _sumSqR += dR * dR; _sumN++;

            if (_autoBalance)
            {
                dL = Math.Clamp(dL * gL, -1, 1);
                dR = Math.Clamp(dR * gR, -1, 1);
                int oL = (int)(dL * 32767), oR = (int)(dR * 32767);
                buf[i] = (byte)(oL & 0xFF); buf[i + 1] = (byte)((oL >> 8) & 0xFF);
                buf[i + 2] = (byte)(oR & 0xFF); buf[i + 3] = (byte)((oR >> 8) & 0xFF);
            }
            float aL = Math.Abs((float)dL), aR = Math.Abs((float)dR);
            if (aL > peakL) peakL = aL;
            if (aR > peakR) peakR = aR;
        }
        _peakL = peakL; _peakR = peakR;
        UpdateBalance();
    }

    private void UpdateBalance()
    {
        // decay shown levels toward block peak
        _showL += (_peakL - _showL) * 0.6f;
        _showR += (_peakR - _showR) * 0.6f;
        LevelsChanged?.Invoke();

        if (!_autoBalance || _sumN < (uint)_rate) return; // ~1s of audio
        var rmsL = Math.Sqrt(_sumSqL / _sumN);
        var rmsR = Math.Sqrt(_sumSqR / _sumN);
        _sumSqL = _sumSqR = 0; _sumN = 0;

        double dbL = 20 * Math.Log10(rmsL + 1e-9);
        double dbR = 20 * Math.Log10(rmsR + 1e-9);
        if (dbL < -50 || dbR < -50) { _imbalancedSince = DateTime.UtcNow; _imbalanceLogged = false; return; } // silence/one dead
        double diff = dbL - dbR; // + => L louder at source
        if (Math.Abs(diff) < 2) { _imbalancedSince = DateTime.UtcNow; _imbalanceLogged = false; return; }
        if (Math.Abs(diff) > 10) return; // assume intentional wide stereo
        if ((DateTime.UtcNow - _imbalancedSince).TotalSeconds < 5) return;

        // slew the weaker channel's boost toward the measured gap (max +6dB)
        if (diff > 0) _gainDbR = Math.Min(6, _gainDbR + 0.5);
        else _gainDbL = Math.Min(6, _gainDbL + 0.5);
        if (!_imbalanceLogged)
        {
            _imbalanceLogged = true;
            _log.Info($"Auto-balance engaged (L {dbL:F1}dB / R {dbR:F1}dB)");
        }
        else if (((_gainDbR + _gainDbL) % 2.0) < 0.5)
        {
            _log.Info($"Auto-balance now +{_gainDbL:F1}dB L / +{_gainDbR:F1}dB R");
        }
    }

    // ---------- libvlc events / info ----------

    private void EnsureLib()
    {
        if (_lib != null) return;
        Core.Initialize();
        _lib = new LibVLC("--no-video", "--no-stats", "--network-caching=" + _cache,
            "--live-caching=" + _cache);
        _log.Info("LibVLC ready (cache " + _cache + "ms)");
    }

    private void HookPlayer(MediaPlayer p)
    {
        p.Buffering += (_, e) =>
        {
            if (State is EngineState.Connecting or EngineState.Reconnecting or EngineState.Buffering)
                SetState(EngineState.Buffering, $"Buffering {e.Cache:0}%…", DetailText);
        };
        p.Playing += (_, _) => { };
        p.EncounteredError += (_, _) => { };
    }

    private void RefreshTrackInfo()
    {
        try
        {
            var tracks = _player?.Media?.Tracks;
            if (tracks == null) return;
            foreach (var t in tracks)
            {
                if (t.TrackType != TrackType.Audio) continue;
                DetailText = FourCC(t.Codec);
                if (t.Data.Audio.Channels > 0) DetailText += $" {t.Data.Audio.Channels}ch";
                if (t.Bitrate > 0) DetailText += $" {t.Bitrate / 1000}k";
                if (t.Data.Audio.Rate > 0) DetailText += $" {t.Data.Audio.Rate}Hz";
                break;
            }
        }
        catch { }
    }

    private string? _meta = "";
    private void PollMeta()
    {
        try
        {
            if (string.IsNullOrEmpty(DetailText)) RefreshTrackInfo();
            var now = _player?.Media?.Meta(MetadataType.NowPlaying);
            var title = string.IsNullOrWhiteSpace(now) ? "" : now;
            if (title != _meta)
            {
                _meta = title;
                if (!string.IsNullOrEmpty(title))
                {
                    DetailText = DetailText.Split(" ♪")[0] + " ♪ " + title;
                    _log.Info("Now playing: " + title);
                    StateChanged?.Invoke();
                }
            }
        }
        catch { }
    }

    private static string FourCC(uint c) =>
        new string(new[] { (char)(c & 0xFF), (char)((c >> 8) & 0xFF), (char)((c >> 16) & 0xFF), (char)((c >> 24) & 0xFF) })
        .Trim('\0', ' ');

    private void SetState(EngineState s, string text, string detail)
    {
        State = s; StatusText = text; DetailText = detail;
        try { App.Current?.Dispatcher.BeginInvoke(() => StateChanged?.Invoke()); }
        catch { StateChanged?.Invoke(); }
    }

    public void Dispose()
    {
        _userStop = true;
        try { _cts?.Cancel(); } catch { }
        try { _player?.Stop(); _player?.Dispose(); } catch { }
        CleanupAudio();
        try { (_lib as IDisposable)?.Dispose(); } catch { }
        _lib = null;
    }
}
