using System.IO;
using System.Net.Sockets;
using System.Text;
using Concentus;
using Concentus.Enums;
using NAudio.CoreAudioApi;
using NAudio.Lame;
using NAudio.Wave;

namespace AdoxicSound.Services;

public enum SendServerType { Icecast2, ShoutcastV2, ShoutcastV1 }
public enum SendCodec { Mp3, Aac, Opus }
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
    public SendCodec Codec { get; set; } = SendCodec.Mp3;
    public string? InputDeviceId { get; set; }
    public bool InputLoopback { get; set; } // capture a speaker output instead of a mic
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
    private DateTime _lastDropBalloon = DateTime.MinValue;

    public SendEngine(Logger log) => _log = log;

    public (float Peak, DateTime Audible) ConsumeMic()
    {
        var p = _micPeak;
        _micPeak = 0;
        return (p, _lastAudibleUtc);
    }

    public DateTime LastAudibleUtc => _lastAudibleUtc;

    private WasapiCapture? _monitor;

    /// <summary>Lightweight level monitoring without broadcasting.</summary>
    public void MonitorStart(string? deviceId, bool loopback)
    {
        MonitorStop();
        try
        {
            _monitor = OpenCapture(deviceId, loopback);
            _monitor.DataAvailable += (_, e) =>
            {
                try { TrackMicPeak(e.Buffer, e.BytesRecorded, _monitor!.WaveFormat); }
                catch { }
            };
            _monitor.StartRecording();
        }
        catch { MonitorStop(); }
    }

    public void MonitorStop()
    {
        try { _monitor?.StopRecording(); } catch { }
        try { _monitor?.Dispose(); } catch { }
        _monitor = null;
    }

    public static List<(string Id, string Name, bool Loopback)> ListInputs()
    {
        var list = new List<(string, string, bool)>();
        try
        {
            using var e = new MMDeviceEnumerator();
            foreach (var d in e.EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active))
            {
                try { list.Add((d.ID, d.FriendlyName, false)); d.Dispose(); }
                catch { }
            }
            foreach (var d in e.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active))
            {
                try { list.Add((d.ID, d.FriendlyName + " (loopback)", true)); d.Dispose(); }
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
            if (App.Current is App app && app.Store.Settings.AlarmEnabled &&
                (DateTime.UtcNow - _lastDropBalloon).TotalMinutes >= 5)
            {
                _lastDropBalloon = DateTime.UtcNow;
                app.NotifyBalloon("Adoxic Sound — push lost", $"{ServerLabel()} — retrying…");
            }
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
            var nch = Math.Max(1, Math.Min(2, (await ProbeCaptureChannels(c.InputDeviceId, c.InputLoopback))));
            if (targetCh == 0) targetCh = nch;
            var req = $"PUT {c.Mount} HTTP/1.1\r\nAuthorization: Basic {cred}\r\n" +
                      $"Host: {c.Host}:{c.Port}\r\nUser-Agent: AdoxicSound/{Version}\r\n" +
                      $"Content-Type: {ContentType(c.Codec)}\r\n" +
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
            sb.Append($"icy-br:{c.Bitrate}\r\nContent-Type:{ContentType(c.Codec)}\r\n\r\n");
            var buf = Encoding.ASCII.GetBytes(sb.ToString());
            await stream.WriteAsync(buf, ct);
            var line = await ReadLine(stream, ct);
            if (!line.StartsWith("OK", StringComparison.OrdinalIgnoreCase))
                throw new Exception("server refused (" + line.Trim() + " " + await ReadBody(stream) + ")");
        }

        // capture -> resample 44100/16bit -> encoder -> socket
        using var cap = OpenCapture(c.InputDeviceId, c.InputLoopback);
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
        using var encoder = CreateEncoder(c, resampler.WaveFormat);
        cap.StartRecording();

        SetState(SendState.Live, "Live");
        LiveSince = DateTime.UtcNow;
        _log.TxInfo($"On air: {ServerLabel()} @ {c.Bitrate}k {c.Codec}");
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
                    encoder.Write(pcm, 0, n);
                    totalPcm += n;
                    var due = clockStart + TimeSpan.FromSeconds(totalPcm / byteRate);
                    var wait = due - DateTime.UtcNow;
                    if (wait > TimeSpan.Zero)
                    {
                        try { await Task.Delay(wait, ct); } catch { break; }
                    }
                    foreach (var chunk in encoder.TakeOutput())
                    {
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

    private static ILiveEncoder CreateEncoder(SendPreset c, WaveFormat pcmFmt) => c.Codec switch
    {
        SendCodec.Aac => new AacLiveEncoder(pcmFmt, c.Bitrate),
        SendCodec.Opus => new OpusLiveEncoder(pcmFmt, c.Bitrate),
        _ => new Mp3LiveEncoder(pcmFmt, c.Bitrate),
    };

    /// <summary>Chunked live audio encoder: PCM in, encoded frames out.</summary>
    private interface ILiveEncoder : IDisposable
    {
        void Write(byte[] pcm, int offset, int count);
        List<byte[]> TakeOutput();
    }

    private sealed class Mp3LiveEncoder : ILiveEncoder
    {
        private readonly MemoryStream _buf = new(65536);
        private readonly LameMP3FileWriter _lame;
        public Mp3LiveEncoder(WaveFormat fmt, int kbps) =>
            _lame = new LameMP3FileWriter(_buf, fmt, ToPreset(kbps));
        public void Write(byte[] pcm, int offset, int count) =>
            _lame.Write(pcm, offset, count); // emits complete frames; never Flush() mid-stream
        public List<byte[]> TakeOutput()
        {
            var out_ = new List<byte[]>();
            if (_buf.Length > 0)
            {
                out_.Add(_buf.ToArray());
                _buf.SetLength(0);
            }
            return out_;
        }
        public void Dispose()
        {
            try { _lame.Dispose(); } catch { }
            try { _buf.Dispose(); } catch { }
        }
    }

    private sealed class AacLiveEncoder : ILiveEncoder
    {
        private readonly FdkAacEncoder _enc;
        private readonly int _channels;
        private readonly List<short> _pending = new();
        private readonly List<byte[]> _stash = new();
        public AacLiveEncoder(WaveFormat fmt, int kbps)
        {
            _channels = Math.Max(1, Math.Min(2, fmt.Channels));
            _enc = new FdkAacEncoder(_channels, fmt.SampleRate, kbps * 1000);
        }
        public void Write(byte[] pcm, int offset, int count)
        {
            int shorts = count / 2;
            var all = new short[_pending.Count + shorts];
            for (int i = 0; i < _pending.Count; i++) all[i] = _pending[i];
            Buffer.BlockCopy(pcm, offset, all, _pending.Count * 2, shorts * 2);
            _pending.Clear();
            int frame = _enc.FrameLength * _channels;
            int at = 0;
            while (at + frame <= all.Length)
            {
                var slice = new short[frame];
                Array.Copy(all, at, slice, 0, frame);
                var adts = _enc.Encode(slice, 0, _enc.FrameLength);
                if (adts.Length > 0) _stash.Add(adts);
                at += frame;
            }
            for (int i = at; i < all.Length; i++) _pending.Add(all[i]);
        }
        public List<byte[]> TakeOutput()
        {
            var out_ = new List<byte[]>(_stash);
            _stash.Clear();
            return out_;
        }
        public void Dispose()
        {
            try { _enc.Dispose(); } catch { }
        }
    }

    private sealed class OpusLiveEncoder : ILiveEncoder
    {
        private readonly MemoryStream _buf = new(65536);
        private readonly Concentus.Oggfile.OpusOggWriteStream _ogg;
        public OpusLiveEncoder(WaveFormat fmt, int kbps)
        {
            var enc = OpusCodecFactory.CreateEncoder(48000, fmt.Channels,
                OpusApplication.OPUS_APPLICATION_AUDIO);
            enc.Bitrate = Math.Clamp(kbps, 8, 320) * 1000;
            var tags = new Concentus.Oggfile.OpusTags();
            _ogg = new Concentus.Oggfile.OpusOggWriteStream(enc, _buf, tags,
                inputSampleRate: fmt.SampleRate, leaveOpen: true);
        }
        public void Write(byte[] pcm, int offset, int count)
        {
            // 16-bit shorts; the writer frames (20ms), resamples to 48k and muxes Ogg
            int shorts = count / 2;
            var s = new short[shorts];
            Buffer.BlockCopy(pcm, offset, s, 0, shorts * 2);
            _ogg.WriteSamples(s, 0, shorts);
        }
        public List<byte[]> TakeOutput()
        {
            var out_ = new List<byte[]>();
            if (_buf.Length > 0)
            {
                out_.Add(_buf.ToArray());
                _buf.SetLength(0);
            }
            return out_;
        }
        public void Dispose()
        {
            try { _buf.Dispose(); } catch { }
        }
    }

    /// <summary>Write-only chunk queue stream (feeds live encoders / sockets).</summary>
    private sealed class ChunkStream : Stream
    {
        private readonly System.Collections.Concurrent.ConcurrentQueue<byte[]> _q = new();
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => 0; set { } }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count)
        {
            var c = new byte[count];
            Buffer.BlockCopy(buffer, offset, c, 0, count);
            _q.Enqueue(c);
        }
        public List<byte[]> TakeAll()
        {
            var out_ = new List<byte[]>();
            while (_q.TryDequeue(out var c)) out_.Add(c);
            return out_;
        }
    }

    private sealed class BlockingFeed : IWaveProvider
    {
        private readonly System.Collections.Concurrent.BlockingCollection<byte[]> _q = new(64);
        private byte[]? _cur;
        private int _pos;
        private bool _done;
        public WaveFormat WaveFormat { get; }
        public BlockingFeed(WaveFormat fmt) => WaveFormat = fmt;
        public void Write(byte[] buf, int offset, int count)
        {
            if (_done) return;
            var c = new byte[count];
            Buffer.BlockCopy(buf, offset, c, 0, count);
            _q.Add(c);
        }
        public void Complete() { _done = true; }
        public int Read(byte[] buffer, int offset, int count)
        {
            int total = 0;
            while (total < count)
            {
                if (_cur == null)
                {
                    if (!_q.TryTake(out _cur, 100))
                    {
                        if (_done) break;
                        continue;
                    }
                    _pos = 0;
                }
                int n = Math.Min(count - total, _cur.Length - _pos);
                Buffer.BlockCopy(_cur, _pos, buffer, offset + total, n);
                total += n;
                _pos += n;
                if (_pos >= _cur.Length) _cur = null;
            }
            return total;
        }
    }

    private static async Task<int> ProbeCaptureChannels(string? id, bool loopback)
    {
        try
        {
            using var cap = OpenCapture(id, loopback);
            await Task.Delay(150);
            return Math.Max(1, Math.Min(2, cap.WaveFormat.Channels));
        }
        catch { return 2; }
    }

    private static WasapiCapture OpenCapture(string? id, bool loopback)
    {
        if (!string.IsNullOrEmpty(id))
        {
            try
            {
                var dev = new MMDeviceEnumerator().GetDevice(id);
                return loopback ? new WasapiLoopbackCapture(dev) : new WasapiCapture(dev);
            }
            catch { }
        }
        return loopback ? new WasapiLoopbackCapture() : new WasapiCapture();
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

    private static string ContentType(SendCodec codec) => codec switch
    {
        SendCodec.Aac => "audio/aac",
        SendCodec.Opus => "audio/ogg",
        _ => "audio/mpeg",
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
        MonitorStop();
        try { _cts?.Cancel(); } catch { }
        _cts = null;
    }
}
