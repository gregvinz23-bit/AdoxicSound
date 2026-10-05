using System.IO;
using NAudio.CoreAudioApi;
using NAudio.Lame;
using NAudio.Wave;

namespace AdoxicSound.Services;

public enum RecordFormat { Mp3, Aac, Wav }
public enum RecordState { Idle, Recording }

/// <summary>Records an input device to timestamped MP3/AAC/WAV files, hourly split.</summary>
public sealed class RecordEngine : IDisposable
{
    private readonly Logger _log;
    private WasapiCapture? _cap;
    private BufferedWaveProvider? _bwp;
    private MediaFoundationResampler? _resampler;
    private LameMP3FileWriter? _lame;
    private WaveFileWriter? _wav;
    private FileStream? _aacFile;
    private Task? _aacTask;
    private BlockingProvider? _aacFeed;
    private FileStream? _outFile;
    private string _currentPath = "";
    private CancellationTokenSource? _cts;
    private DateTime _fileStartUtc;
    private long _fileBytes;

    public event Action? StateChanged;

    public RecordState State { get; private set; } = RecordState.Idle;
    public string StatusText { get; private set; } = "Not recording";
    public string CurrentFile => _currentPath;
    public DateTime? RecordSince { get; private set; }

    public static bool AacAvailable { get; } = ProbeAac();

    public RecordEngine(Logger log) => _log = log;

    private static bool ProbeAac()
    {
        try
        {
            var fmt = new WaveFormat(44100, 16, 2);
            var silence = new byte[fmt.AverageBytesPerSecond / 5];
            using var ms = new MemoryStream();
            using var src = new RawSourceWaveStream(new MemoryStream(silence), fmt);
            MediaFoundationEncoder.EncodeToAac(src, ms, 96000);
            return ms.Length > 0;
        }
        catch { return false; }
    }

    public string Folder { get; set; } = "";
    public RecordFormat Format { get; set; } = RecordFormat.Mp3;
    public int Bitrate { get; set; } = 128;
    public string? InputDeviceId { get; set; }
    public bool InputLoopback { get; set; }

    public void Start()
    {
        Stop(silent: true);
        var folder = string.IsNullOrWhiteSpace(Folder)
            ? Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "records")
            : Folder;
        Directory.CreateDirectory(folder);
        _cts = new CancellationTokenSource();
        OpenFile(folder);
        var cap = OpenCapture();
        _cap = cap;
        var inFmt = cap.WaveFormat;
        int ch = Math.Max(1, Math.Min(2, inFmt.Channels));
        _bwp = new BufferedWaveProvider(inFmt)
        {
            BufferDuration = TimeSpan.FromSeconds(5),
            DiscardOnBufferOverflow = true
        };
        cap.DataAvailable += (_, e) =>
        {
            try { _bwp?.AddSamples(e.Buffer, 0, e.BytesRecorded); } catch { }
        };
        var pcmFmt = new WaveFormat(44100, 16, ch);
        _resampler = new MediaFoundationResampler(_bwp, pcmFmt);
        StartEncoder(pcmFmt);
        cap.StartRecording();
        RecordSince = DateTime.UtcNow;
        _recPcmTotal = 0;
        _recClockStart = DateTime.UtcNow;
        SetState(RecordState.Recording, "Recording");
        _log.Info($"Recording {Format} → {Path.GetFileName(_currentPath)}");
        Task.Run(() => SplitLoop(_cts.Token));
        Task.Run(() => PumpLoop(_cts.Token));
    }

    public void Stop(bool silent = false)
    {
        try { _cts?.Cancel(); } catch { }
        _cts = null;
        try { _cap?.StopRecording(); } catch { }
        CloseEncoder();
        try { _cap?.Dispose(); } catch { }
        _cap = null;
        try { _resampler?.Dispose(); } catch { }
        _resampler = null;
        _bwp = null;
        if (State != RecordState.Idle)
        {
            SetState(RecordState.Idle, "Not recording");
            if (!silent && !string.IsNullOrEmpty(_currentPath))
                _log.Info($"Record stopped: {Path.GetFileName(_currentPath)}");
        }
        RecordSince = null;
    }

    private WasapiCapture OpenCapture()
    {
        var id = InputDeviceId;
        if (!string.IsNullOrEmpty(id))
        {
            try
            {
                var dev = new MMDeviceEnumerator().GetDevice(id);
                return InputLoopback ? new WasapiLoopbackCapture(dev) : new WasapiCapture(dev);
            }
            catch { }
        }
        return InputLoopback ? new WasapiLoopbackCapture() : new WasapiCapture();
    }

    private void OpenFile(string folder)
    {
        var ext = Format switch { RecordFormat.Aac => "m4a", RecordFormat.Wav => "wav", _ => "mp3" };
        _currentPath = Path.Combine(folder, $"rec_{DateTime.Now:yyyyMMdd-HHmmss}.{ext}");
        _fileStartUtc = DateTime.UtcNow;
        _fileBytes = 0;
        _outFile = new FileStream(_currentPath, FileMode.Create, FileAccess.Write, FileShare.Read);
    }

    private void StartEncoder(WaveFormat pcmFmt)
    {
        if (_outFile == null) return;
        if (Format == RecordFormat.Wav)
        {
            _wav = new WaveFileWriter(_outFile, pcmFmt);
            _outFile = null; // writer owns it
        }
        else if (Format == RecordFormat.Aac)
        {
            _aacFile = _outFile;
            _outFile = null;
            _aacFeed = new BlockingProvider(pcmFmt);
            var feed = _aacFeed;
            var file = _aacFile;
            var rate = Bitrate;
            _aacTask = Task.Run(() =>
            {
                try { MediaFoundationEncoder.EncodeToAac(feed, file, rate * 1000); }
                catch (Exception ex) { _log.Error("AAC encode failed: " + ex.Message); }
            });
        }
        else
        {
            _lame = new LameMP3FileWriter(_outFile, pcmFmt, ToLamePreset(Bitrate));
            _outFile = null;
        }
    }

    private void CloseEncoder()
    {
        try
        {
            if (_aacFeed != null) { _aacFeed.Complete(); _aacTask?.Wait(TimeSpan.FromSeconds(5)); }
        }
        catch { }
        _aacFeed = null;
        _aacTask = null;
        try { _lame?.Dispose(); } catch { }
        _lame = null;
        try { _wav?.Dispose(); } catch { }
        _wav = null;
        try { _aacFile?.Dispose(); } catch { }
        _aacFile = null;
        try { _outFile?.Dispose(); } catch { }
        _outFile = null;
    }

    private async Task SplitLoop(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested && State == RecordState.Recording)
            {
                await Task.Delay(TimeSpan.FromMinutes(1), ct);
                if (DateTime.UtcNow - _fileStartUtc < TimeSpan.FromHours(1)) continue;
                var folder = Path.GetDirectoryName(_currentPath) ?? ".";
                _log.Info("Hourly split: " + Path.GetFileName(_currentPath));
                var fmt = _resampler?.WaveFormat;
                CloseEncoder();
                OpenFile(folder);
                if (fmt != null) StartEncoder(fmt);
            }
        }
        catch { }
    }

    private long _recPcmTotal;
    private DateTime _recClockStart = DateTime.UtcNow;

    private async Task PumpLoop(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested && State == RecordState.Recording)
            {
                if (!Pump()) await Task.Delay(20, ct);
            }
        }
        catch { }
    }

    /// <summary>Moves audio to file, paced to realtime (resampler emits faster than live).</summary>
    /// <returns>True if any PCM was consumed.</returns>
    private bool Pump()
    {
        if (State != RecordState.Recording || _resampler == null) return false;
        try
        {
            var rate = (double)_resampler.WaveFormat.AverageBytesPerSecond;
            var buf = new byte[16384];
            bool worked = false;
            int guard = 0, n;
            while (guard++ < 8 && (n = _resampler.Read(buf, 0, buf.Length)) > 0)
            {
                worked = true;
                if (_lame != null) _lame.Write(buf, 0, n);
                else if (_wav != null) _wav.Write(buf, 0, n);
                else if (_aacFeed != null) _aacFeed.Write(buf, 0, n);
                _fileBytes += n;
                _recPcmTotal += n;
            }
            if (!worked) { _recPcmTotal = 0; _recClockStart = DateTime.UtcNow; return false; }
            var due = _recClockStart + TimeSpan.FromSeconds(_recPcmTotal / rate);
            var wait = due - DateTime.UtcNow;
            if (wait > TimeSpan.Zero && wait < TimeSpan.FromSeconds(2))
                System.Threading.Thread.Sleep(wait);
            else if (wait <= TimeSpan.FromSeconds(-2)) { _recPcmTotal = 0; _recClockStart = DateTime.UtcNow; }
            return true;
        }
        catch { return false; }
    }

    public string FileSummary()
    {
        if (string.IsNullOrEmpty(_currentPath)) return "";
        var up = RecordSince is DateTime t ? DateTime.UtcNow - t : TimeSpan.Zero;
        return $"{Path.GetFileName(_currentPath)} · {up:hh\\:mm\\:ss}";
    }

    public List<(string Name, string Size)> TodayFiles()
    {
        var list = new List<(string Name, string Size)>();
        try
        {
            var folder = string.IsNullOrWhiteSpace(Folder)
                ? Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "records")
                : Folder;
            if (!Directory.Exists(folder)) return list;
            var today = DateTime.Now.ToString("yyyyMMdd");
            foreach (var f in Directory.GetFiles(folder, $"rec_{today}-*.*"))
            {
                var fi = new FileInfo(f);
                list.Add((fi.Name, $"{fi.Length / 1048576.0:0.0}MB"));
            }
            list.Sort((a, b) => string.Compare(b.Name, a.Name, StringComparison.Ordinal));
        }
        catch { }
        return list;
    }

    private static NAudio.Lame.LAMEPreset ToLamePreset(int kbps) => kbps switch
    {
        <= 64 => NAudio.Lame.LAMEPreset.ABR_64,
        <= 96 => NAudio.Lame.LAMEPreset.ABR_96,
        <= 128 => NAudio.Lame.LAMEPreset.ABR_128,
        <= 160 => NAudio.Lame.LAMEPreset.ABR_160,
        <= 256 => NAudio.Lame.LAMEPreset.ABR_256,
        _ => NAudio.Lame.LAMEPreset.ABR_320,
    };

    private void SetState(RecordState s, string text)
    {
        State = s;
        StatusText = text;
        try { System.Windows.Application.Current?.Dispatcher.BeginInvoke(() => StateChanged?.Invoke()); }
        catch { StateChanged?.Invoke(); }
    }

    public void Dispose()
    {
        try { Stop(silent: true); } catch { }
    }

    private sealed class BlockingProvider : IWaveProvider
    {
        private readonly System.Collections.Concurrent.BlockingCollection<byte[]> _q = new(64);
        private byte[]? _cur;
        private int _pos;
        private bool _done;
        public WaveFormat WaveFormat { get; }
        public BlockingProvider(WaveFormat fmt) => WaveFormat = fmt;
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
                        if (_done && _q.Count == 0) break;
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
}
