using System.IO;
using System.Net.Sockets;
using System.Text;
using NAudio.CoreAudioApi;
using NAudio.Lame;
using NAudio.Wave;

namespace AdoxicSound.Services;

public enum SendServerType { Icecast2, ShoutcastV2, ShoutcastV1 }
public enum SendState { Stopped, Connecting, Live, Reconnecting }

public sealed class SendPreset
{
    public string Name { get; set; } = "New server";
    public SendServerType Type { get; set; } = SendServerType.Icecast2;
    public string Host { get; set; } = "";
    public int Port { get; set; } = 8000;
    public string Mount { get; set; } = "/live";
    public string User { get; set; } = "source";
    public string PassB64 { get; set; } = ""; // lightly obfuscated, local file only
    public int Bitrate { get; set; } = 128;
    public string? InputDeviceId { get; set; }
    public int Channels { get; set; } = 0; // 0 auto, 1 mono, 2 stereo
    public int CutoffMin { get; set; } = 0; // 0 off; auto-stop after N silent minutes
    public int ReconnectSec { get; set; } = 4;
}

/// <summary>Mic/line-in capture -> MP3 -> Icecast2 / Shoutcast v1+v2 (legacy source mode).</summary>
public sealed class SendEngine : IDisposable
{
    private readonly Logger _log;
    private CancellationTokenSource? _cts;
    private Task? _loop;
    private volatile bool _userStop;
    private volatile float _micPeak;
    private DateTime _lastAudibleUtc = DateTime.UtcNow;

    public event Action? StateChanged;

    public SendState State { get; private set; } = SendState.Stopped;
    public string StatusText { get; private set; } = "Off air";
    public SendPreset Config { get; private set; } = new();
    public long BytesSent { get; private set; }
    public int Drops { get; private set; }
    public DateTime? LiveSince { get; private set; }
    public int Attempt { get; private set; }
    public string Version { get; set; } = "?";

    public SendEngine(Logger log) => _log = log;

    public (float Peak, DateTime Audible) ConsumeMic()
    {
        var p = _micPeak;
        _micPeak = 0;
        return (p, _lastAudibleUtc);
    }

    public DateTime LastAudibleUtc => _lastAudibleUtc;

    public static List<(string Id, string Name)> ListInputs()
    {
        var list = new List<(string, string)>();
        try
        {
            using var e = new MMDeviceEnumerator();
            foreach (var d in e.EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active))
            {
                try { list.Add((d.ID, d.FriendlyName)); d.Dispose(); }
                catch { }
            }
        }
        catch { }
        return list;
    }

    public void Start(SendPreset cfg)
    {
        Stop(silent: true);
        _userStop = false;
        Config = cfg;
        Attempt = 0;
        BytesSent = 0;
        Drops = 0;
        LiveSince = null;
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;
        _loop = Task.Run(() => RunLoop(ct));
        _loop.ContinueWith(t =>
        {
            if (t.IsFaulted)
                _log.TxError("Send engine crashed: " + (t.Exception?.GetBaseException().Message ?? "?"));
        });
    }

    public void Stop(bool silent = false)
    {
        _userStop = true;
        try { _cts?.Cancel(); } catch { }
        _cts = null;
        if (State != SendState.Stopped)
        {
            SetState(SendState.Stopped, "Off air");
            if (!silent) _log.TxInfo($"Off air ({BytesSent / 1024}KB sent, {Drops} drops)");
        }
    }

    private async Task RunLoop(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested && !_userStop)
        {
            Attempt++;
            SetState(Attempt > 1 ? SendState.Reconnecting : SendState.Connecting,
                Attempt > 1 ? $"Reconnecting #{Attempt}…" : "Connecting…");
            try
            {
                if (await TrySend(ct)) return;
            }
            catch (OperationCanceledException) { return; }
            catch (Exception ex) { _log.TxWarn($"Send attempt #{Attempt}: {ex.Message}"); }
            if (_userStop || ct.IsCancellationRequested) return;
            Drops++;
            var wait = Math.Clamp(Config.ReconnectSec, 2, 120);
            SetState(SendState.Reconnecting, $"Reconnecting #{Attempt} in {wait}s…");
            _log.TxError($"Push lost — retry #{Attempt + 1} in {wait}s…");
            try { await Task.Delay(wait * 1000, ct); } catch { return; }
        }
    }

    private async Task<bool> TrySend(CancellationToken ct)
    {
        var c = Config;
        using var tcp = new TcpClient();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(12));
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeout.Token);
        await tcp.ConnectAsync(c.Host, c.Port, linked.Token);
        int targetCh = c.Channels == 1 ? 1 : c.Channels == 2 ? 2 : 0; // 0 = follow device
        var stream = tcp.GetStream();
        stream.WriteTimeout = 10000;
        stream.ReadTimeout = 10000;

        if (c.Type == SendServerType.Icecast2)
        {
            var cred = Convert.ToBase64String(Encoding.ASCII.GetBytes($"{c.User}:{Decode(c.PassB64)}"));
            var nch = Math.Max(1, Math.Min(2, (await ProbeCaptureChannels(c.InputDeviceId))));
            if (targetCh == 0) targetCh = nch;
            var req = $"PUT {c.Mount} HTTP/1.1\r\nAuthorization: Basic {cred}\r\n" +
                      $"Host: {c.Host}:{c.Port}\r\nUser-Agent: AdoxicSound/{Version}\r\n" +
                      "Content-Type: audio/mpeg\r\n" +
                      $"ice-bitrate: {c.Bitrate}\r\nice-name: Adoxic Sound\r\nice-public: 0\r\n" +
                      $"ice-audio-info: ice-bitrate={c.Bitrate};ice-channels={targetCh};ice-samplerate=44100\r\n" +
                      "Expect: 100-continue\r\n\r\n";
            var buf = Encoding.ASCII.GetBytes(req);
            await stream.WriteAsync(buf, ct);
            var line = await ReadLine(stream, ct);
            if (line.StartsWith("HTTP/1", StringComparison.OrdinalIgnoreCase) && line.Contains(" 100"))
            {
                _log.TxInfo("Server sent 100-continue, streaming");
            }
            else if (!line.Contains("200"))
                throw new Exception("server refused (" + line.Trim() + " " + await ReadBody(stream) + ")");
        }
        else
        {
            // Shoutcast v1 + v2 (DNAS 2.x accepts legacy source mode for any stream)
            var sb = new StringBuilder();
            sb.Append(Decode(c.PassB64)).Append('\n');
            sb.Append("icy-name:Adoxic Sound\r\nicy-genre:Misc\r\nicy-url:\r\nicy-pub:1\r\n");
            sb.Append($"icy-br:{c.Bitrate}\r\nContent-Type:audio/mpeg\r\n\r\n");
            var buf = Encoding.ASCII.GetBytes(sb.ToString());
            await stream.WriteAsync(buf, ct);
            var line = await ReadLine(stream, ct);
            if (!line.StartsWith("OK", StringComparison.OrdinalIgnoreCase))
                throw new Exception("server refused (" + line.Trim() + " " + await ReadBody(stream) + ")");
        }

        // capture -> resample 44100/16bit -> MP3 -> socket
        using var cap = OpenCapture(c.InputDeviceId);
        var inFmt = cap.WaveFormat;
        int srcCh = Math.Max(1, Math.Min(2, inFmt.Channels));
        if (targetCh < 1 || targetCh > 2) targetCh = srcCh;
        var pcmFmt = new WaveFormat(44100, 16, targetCh);
        var bwp = new BufferedWaveProvider(inFmt)
        {
            BufferDuration = TimeSpan.FromSeconds(3),
            DiscardOnBufferOverflow = true
        };
        cap.DataAvailable += (_, e) =>
        {
            try
            {
                TrackMicPeak(e.Buffer, e.BytesRecorded, inFmt);
                bwp.AddSamples(e.Buffer, 0, e.BytesRecorded);
            }
            catch { }
        };
        using var resampler = new MediaFoundationResampler(bwp, pcmFmt);
        using var mp3buf = new MemoryStream(65536);
        using var lame = new LameMP3FileWriter(mp3buf, resampler.WaveFormat, ToPreset(c.Bitrate));
        cap.StartRecording();

        SetState(SendState.Live, "Live");
        LiveSince = DateTime.UtcNow;
        _log.TxInfo($"On air: {ServerLabel()} @ {c.Bitrate}k");
        var pcm = new byte[16384];
        // pace to realtime: the resampler emits as fast as pulled, so throttle
        // consumption to the PCM clock or we'd flood the server with silence
        var byteRate = (double)resampler.WaveFormat.AverageBytesPerSecond;
        long totalPcm = 0;
        var clockStart = DateTime.UtcNow;
        try
        {
            while (!ct.IsCancellationRequested && !_userStop && tcp.Connected)
            {
                if (c.CutoffMin > 0 && (DateTime.UtcNow - _lastAudibleUtc).TotalMinutes >= c.CutoffMin)
                {
                    _log.TxWarn($"Auto-stopped: mic silent {c.CutoffMin} min");
                    _userStop = true;
                    return true;
                }
                int n = resampler.Read(pcm, 0, pcm.Length);
                if (n > 0)
                {
                    lame.Write(pcm, 0, n); // emits complete frames; never Flush() mid-stream
                    totalPcm += n;
                    var due = clockStart + TimeSpan.FromSeconds(totalPcm / byteRate);
                    var wait = due - DateTime.UtcNow;
                    if (wait > TimeSpan.Zero)
                    {
                        try { await Task.Delay(wait, ct); } catch { break; }
                    }
                    if (mp3buf.Length > 0)
                    {
                        var chunk = mp3buf.ToArray();
                        mp3buf.SetLength(0);
                        await stream.WriteAsync(chunk, ct);
                        BytesSent += chunk.Length;
                    }
                }
                else
                {
                    try { await Task.Delay(20, ct); } catch { break; }
                    totalPcm = 0; // starved: restart clock so we don't burst afterwards
                    clockStart = DateTime.UtcNow;
                }
            }
        }
        finally { try { cap.StopRecording(); } catch { } }
        return _userStop;
    }

    private static async Task<int> ProbeCaptureChannels(string? id)
    {
        try
        {
            using var cap = OpenCapture(id);
            await Task.Delay(150);
            return Math.Max(1, Math.Min(2, cap.WaveFormat.Channels));
        }
        catch { return 2; }
    }

    private static WasapiCapture OpenCapture(string? id)
    {
        if (!string.IsNullOrEmpty(id))
        {
            try { return new WasapiCapture(new MMDeviceEnumerator().GetDevice(id)); }
            catch { }
        }
        return new WasapiCapture(); // default input
    }

    private void TrackMicPeak(byte[] buf, int bytes, WaveFormat fmt)
    {
        try
        {
            float peak = 0;
            if (fmt.Encoding == WaveFormatEncoding.IeeeFloat)
            {
                for (int i = 0; i + 3 < bytes; i += 4)
                {
                    float s = Math.Abs(BitConverter.ToSingle(buf, i));
                    if (s > peak) peak = s;
                }
            }
            else if (fmt.Encoding == WaveFormatEncoding.Pcm && fmt.BitsPerSample == 16)
            {
                for (int i = 0; i + 1 < bytes; i += 2)
                {
                    float s = Math.Abs(BitConverter.ToInt16(buf, i) / 32768f);
                    if (s > peak) peak = s;
                }
            }
            if (peak > _micPeak) _micPeak = peak;
            if (peak > 0.0032f) _lastAudibleUtc = DateTime.UtcNow;
        }
        catch { }
    }

    private static LAMEPreset ToPreset(int kbps) => kbps switch
    {
        <= 64 => LAMEPreset.ABR_64,
        <= 96 => LAMEPreset.ABR_96,
        <= 128 => LAMEPreset.ABR_128,
        <= 160 => LAMEPreset.ABR_160,
        <= 256 => LAMEPreset.ABR_256,
        _ => LAMEPreset.ABR_320,
    };

    private string ServerLabel() =>
        $"{Config.Type} {Config.Host}:{Config.Port}{Config.Mount}";

    private static string Decode(string b64)
    {
        try { return Encoding.UTF8.GetString(Convert.FromBase64String(b64)); }
        catch { return b64; }
    }

    public static string Encode(string plain) =>
        Convert.ToBase64String(Encoding.UTF8.GetBytes(plain ?? ""));

    private static async Task<string> ReadBody(NetworkStream s)
    {
        try
        {
            var sb = new StringBuilder();
            var buf = new byte[512];
            s.ReadTimeout = 3000;
            var sw = System.Diagnostics.Stopwatch.StartNew();
            while (sb.Length < 300 && sw.Elapsed.TotalSeconds < 4)
            {
                int n;
                try { n = await s.ReadAsync(buf, 0, buf.Length); }
                catch { break; }
                if (n <= 0) break;
                sb.Append(Encoding.ASCII.GetString(buf, 0, n));
                if (!s.DataAvailable) break;
            }
            var t = sb.ToString();
            var bi = t.IndexOf("<?xml", StringComparison.Ordinal);
            if (bi >= 0) t = t[bi..];
            t = t.Replace("\r", " ").Replace("\n", " ").Trim();
            return t.Length > 300 ? t[..300] : t;
        }
        catch { return ""; }
    }

    private static async Task<string> ReadLine(NetworkStream s, CancellationToken ct)
    {
        var sb = new StringBuilder();
        var one = new byte[1];
        for (int i = 0; i < 512; i++)
        {
            int n = await s.ReadAsync(one, ct);
            if (n == 0) break;
            if (one[0] == '\n') break;
            if (one[0] != '\r') sb.Append((char)one[0]);
        }
        return sb.ToString();
    }

    private void SetState(SendState s, string text)
    {
        State = s;
        StatusText = text;
        try { System.Windows.Application.Current?.Dispatcher.BeginInvoke(() => StateChanged?.Invoke()); }
        catch { StateChanged?.Invoke(); }
    }

    public void Dispose()
    {
        try { _cts?.Cancel(); } catch { }
        _cts = null;
    }
}
