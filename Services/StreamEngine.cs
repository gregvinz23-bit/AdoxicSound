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

    // meters (linear 0..1 block peak, written by audio thread, consumed by UI)
    private volatile float _peakL, _peakR;

    // auto-balance removed: meters show the raw source signal

    public event Action? StateChanged;

    public EngineState State { get; private set; } = EngineState.Stopped;
    public string StatusText { get; private set; } = "Ready";
    public string DetailText { get; private set; } = "";
    public int Attempt => _attempt;
    public string? ActiveDeviceId { get; private set; }
    public string CurrentUrl => _url;
    public bool IsPlayingAudio => State == EngineState.Playing && !_noOutput;

    public StreamEngine(Logger log) => _log = log;

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
        _sessionStarted = false;
        _healthySec = 0;
        _sessionDrops = 0;
        _underruns = 0;
        _underrunArmed = true;
        _downSince = null;
        _dropReason = null;
        _silenceAlarmed = false;
        _lastAudibleUtc = DateTime.UtcNow;
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
        if (State == EngineState.Stopped) return;
        _userStop = true;
        var up = TimeSpan.FromSeconds(_healthySec);
        var drops = _sessionDrops;
        var url = ShortUrl();
        FlushLifetime();
        StopInternal(user: true);
        _downSince = null;
        _log.Info($"Stopped ({url} — session Up {up:hh\\:mm\\:ss}, Drops {drops})");
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
        if (user) { _peakL = _peakR = 0; }
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
            // MMS fallback: 3rd attempt tries http:// variant (MMSH-over-HTTP)
            if (_attempt >= 3 && current.StartsWith("mms://", StringComparison.OrdinalIgnoreCase))
            {
                current = "http://" + current[6..];
                _log.Warn($"MMS retry via fallback {current}");
            }
            try
            {
                var ok = await TryOnce(current, ct);
                if (ok) return; // played until user stopped
                RegisterDrop(_dropReason ?? "reconnect");
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

        if (!_player.Play()) { _log.Error("Player refused to start"); _dropReason = "start refused"; return false; }

        // wait for first audio (or error/end) up to 20s
        var okStart = await WaitForAudioOrDone(played.Task, TimeSpan.FromSeconds(20), ct);
        if (!okStart) { _log.Warn("No audio received (timeout)"); _dropReason = "no audio (timeout)"; return false; }

        SetState(EngineState.Playing, "Playing", "");
        RefreshTrackInfo();
        _log.Info("Playing: " + DetailText);
        NoteRecovered();

        // health watchdog, 1s tick: stall => reconnect, silence => alarm
        while (!ct.IsCancellationRequested && !_userStop)
        {
            var done = await Task.WhenAny(played.Task, Task.Delay(1000, ct));
            if (done == played.Task) { _dropReason = "ended/error"; return _userStop; } // false => reconnect
            if (IsPlayingAudio) _healthySec++;
            var gap = DateTime.UtcNow - new DateTime(Interlocked.Read(ref _pcmTicks), DateTimeKind.Utc);
            if (!_noOutput && _player?.IsPlaying == true && gap > TimeSpan.FromSeconds(5))
            {
                _log.Warn("Stall: no audio for 5s");
                _dropReason = "stall 5s";
                return false;
            }
            CheckSilence();
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
        try
        {
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
            ActiveDeviceId = _device.ID;
            if (_noOutput)
            {
                _noOutput = false;
                _log.Info("Output restored: " + _device.FriendlyName);
                NoteRecovered();
            }
            else _log.Info($"Output: {_device.FriendlyName} @ {_rate}Hz");
        }
        catch
        {
            CleanupAudio();
            ActiveDeviceId = null;
            if (!_noOutput)
            {
                _noOutput = true;
                _log.Error("No audio output available — waiting for device");
                SetState(State, "No output — waiting for device", DetailText);
            }
        }
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
            if (_noOutput || _tap == null)
            {
                Interlocked.Exchange(ref _pcmTicks, DateTime.UtcNow.Ticks);
                return; // hold the stream, drop PCM until a device exists
            }
            int bytes = checked((int)count * 4); // S16N stereo
            if (bytes <= 0 || bytes > 1 << 20) return;
            // keep the speaker buffer shallow (~500ms): if decode outruns playback
            // (stall recovery, preroll), drop stale backlog so meters+sound stay live
            var cap = _tap.WaveFormat.AverageBytesPerSecond / 2;
            if (_tap.BufferedBytes > cap) _tap.ClearBuffer();
            // buffer underrun watch: speaker starved (glitch) -> count transitions only
            var lowMark = _tap.WaveFormat.AverageBytesPerSecond / 5;
            if (_tap.BufferedBytes < lowMark)
            {
                if (_underrunArmed) { _underruns++; _underrunArmed = false; }
            }
            else if (_tap.BufferedBytes > _tap.WaveFormat.AverageBytesPerSecond) _underrunArmed = true;
            var buf = new byte[bytes];
            Marshal.Copy(samples, buf, 0, bytes);
            ProcessPcm16(buf);
            _tap?.AddSamples(buf, 0, bytes);
            Interlocked.Exchange(ref _pcmTicks, DateTime.UtcNow.Ticks);
            if (!_sessionStarted)
            {
                _sessionStarted = true;
                _sessionStartUtc = DateTime.UtcNow;
            }
            if (_peakL > 0.0032f || _peakR > 0.0032f) // audible (~-50dB)
            {
                if (_silenceAlarmed)
                {
                    _silenceAlarmed = false;
                    _log.Info("Sound back after silence");
                }
                _lastAudibleUtc = DateTime.UtcNow;
            }
        }
        catch { }
    }

    private void ProcessPcm16(byte[] buf)
    {
        // raw source peaks for meters; no DSP — what the station sends is what you see
        float peakL = 0, peakR = 0;
        for (int i = 0; i + 3 < buf.Length; i += 4)
        {
            short sL = (short)(buf[i] | (buf[i + 1] << 8));
            short sR = (short)(buf[i + 2] | (buf[i + 3] << 8));
            float aL = Math.Abs(sL / 32768f), aR = Math.Abs(sR / 32768f);
            if (aL > peakL) peakL = aL;
            if (aR > peakR) peakR = aR;
        }
        _peakL = peakL; _peakR = peakR;
    }

    /// <summary>UI thread: grab latest block peaks (time-based smoothing happens caller-side).</summary>
    public (float L, float R) ConsumePeaks()
    {
        var l = _peakL; var r = _peakR;
        _peakL = 0; _peakR = 0;
        return (l, r);
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

    private string? _dropReason;
    private string? _meta = "";
    private bool _noOutput;
    private DateTime _lastAudibleUtc = DateTime.UtcNow;
    private DateTime _sessionStartUtc;
    private bool _sessionStarted;
    private long _healthySec;
    private int _sessionDrops;
    private long _underruns;
    private bool _underrunArmed = true;
    private DateTime _lastDropAlarm = DateTime.MinValue;
    private DateTime _lastSilenceAlarm = DateTime.MinValue;
    private bool _silenceAlarmed;
    private DateTime? _downSince;
    private int _alarmsToday;
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

    // ---------- drops, alarms, stats ----------

    public int SessionDrops => _sessionDrops;
    public long SessionHealthySec => _healthySec;
    public long Underruns => _underruns;
    public int AlarmsToday => _alarmsToday;
    public DateTime? DownSince => _downSince;

    public string StatsLine(out string lifetime)
    {
        lifetime = "";
        var up = _sessionStarted
            ? TimeSpan.FromSeconds(_healthySec).ToString(@"hh\:mm\:ss")
            : "--:--:--";
        var s = $"Up {up} · Drops {_sessionDrops} · Glitches {_underruns} · Alarms {_alarmsToday}";
        try
        {
            if (App.Current is App app && app.Store.Settings.UrlStats.TryGetValue(_url, out var r))
                lifetime = $"Lifetime: {r.Drops} drops · {TimeSpan.FromSeconds(r.HealthySec):hh\\:mm\\:ss} healthy";
        }
        catch { }
        return s;
    }

    private UrlStatRecord Lifetime()
    {
        if (App.Current is not App app) return new UrlStatRecord();
        var d = app.Store.Settings.UrlStats;
        if (!d.TryGetValue(_url, out var r)) { r = new UrlStatRecord(); d[_url] = r; }
        return r;
    }

    private void FlushLifetime()
    {
        try
        {
            if (string.IsNullOrEmpty(_url)) return;
            var r = Lifetime();
            r.HealthySec += _healthySec;
            _healthySec = 0;
            if (App.Current is App app) app.Store.SaveSettings();
        }
        catch { }
    }

    private void RegisterDrop(string reason)
    {
        _sessionDrops++;
        try
        {
            var r = Lifetime();
            r.Drops++;
            r.HealthySec += _healthySec;
            _healthySec = 0;
            if (App.Current is App app) app.Store.SaveSettings();
        }
        catch { }
        _downSince ??= DateTime.UtcNow;
        if (App.Current is not App app2 || !app2.Store.Settings.AlarmEnabled) return;
        if ((DateTime.UtcNow - _lastDropAlarm).TotalMinutes < 5) return;
        _lastDropAlarm = DateTime.UtcNow;
        _alarmsToday++;
        _log.Error($"Stream lost ({reason}) — retrying…");
        app2.NotifyBalloon("AoIP RX — stream lost", $"{ShortUrl()} ({reason}) — retrying…");
    }

    private void NoteRecovered()
    {
        if (_downSince == null) return;
        var gap = DateTime.UtcNow - _downSince.Value;
        _downSince = null;
        _log.Info($"Recovered after {gap:hh\\:mm\\:ss}");
    }

    private void CheckSilence()
    {
        if (App.Current is not App app || !app.Store.Settings.AlarmEnabled) return;
        if (!IsPlayingAudio || _silenceAlarmed) return;
        var quietFor = (DateTime.UtcNow - _lastAudibleUtc).TotalSeconds;
        if (quietFor < app.Store.Settings.SilenceSeconds) return;
        _silenceAlarmed = true;
        if ((DateTime.UtcNow - _lastSilenceAlarm).TotalMinutes < 5) return;
        _lastSilenceAlarm = DateTime.UtcNow;
        _alarmsToday++;
        _log.Error($"Silence {(int)quietFor}s on {ShortUrl()}");
        app.NotifyBalloon("AoIP RX — silence", $"No audio for {(int)quietFor}s on {ShortUrl()}");
    }

    private string ShortUrl()
    {
        var u = _url;
        return u.Length > 42 ? u[..42] + "…" : u;
    }

    /// <summary>Called when the active output device disappears: fall back to default.</summary>
    public void OnOutputLost(string deviceId)
    {
        if (State == EngineState.Stopped) return;
        if (!string.IsNullOrEmpty(ActiveDeviceId) && ActiveDeviceId != deviceId) return;
        _log.Error("Output device lost — switching to default");
        try
        {
            if (App.Current is App app)
            {
                app.Store.Settings.OutputDeviceId = null;
                app.Store.SaveSettings();
            }
        }
        catch { }
        CleanupAudio();
        ActiveDeviceId = null;
        _dropReason = "output lost";
        RegisterDrop("output lost");
    }

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
