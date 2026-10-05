using System.IO;
using System.Text.Json;

namespace AdoxicSound.Services;

public sealed class Settings
{
    public string? OutputDeviceId { get; set; }
    public int SampleRate { get; set; } = 48000;
    public bool FastMode { get; set; } = true;
    public bool TrayOnClose { get; set; } = true;
    public bool StartOnBoot { get; set; } = false;
    public bool GoLiveOnBoot { get; set; } = false;
    public bool AlarmEnabled { get; set; } = true;
    public int SilenceSeconds { get; set; } = 30;
    public string RecFolder { get; set; } = "";
    public string? RecInputId { get; set; }
    public bool RecLoopback { get; set; }
    public int RecFormat { get; set; } = 0;
    public int RecRate { get; set; } = 128;
    public bool RecAutoStart { get; set; } = false;
    public int RecSplitMin { get; set; } = 60;
    public string? LastUrl { get; set; }
    public double SetWinX { get; set; } = -1;
    public double SetWinY { get; set; } = -1;
    public double LogWinX { get; set; } = -1;
    public double LogWinY { get; set; } = -1;
    public double TxLogWinX { get; set; } = -1;
    public double TxLogWinY { get; set; } = -1;
    public Dictionary<string, UrlStatRecord> UrlStats { get; set; } = new();
}

public sealed class UrlStatRecord
{
    public long Drops { get; set; }
    public long HealthySec { get; set; }
}

public sealed class StreamEntry
{
    public string Name { get; set; } = "";
    public string Url { get; set; } = "";
}

public sealed class StreamsFile
{
    public List<StreamEntry> Urls { get; set; } = new();
}

public sealed class ServersFile
{
    public List<SendPreset> Presets { get; set; } = new();
}

/// <summary>Portable store: streams.json + settings.json next to the exe.</summary>
public sealed class AppStore
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };
    private readonly string _base;

    public Settings Settings { get; private set; } = new();
    public StreamsFile Streams { get; private set; } = new();
    public ServersFile Servers { get; private set; } = new();

    public AppStore()
    {
        _base = AppDomain.CurrentDomain.BaseDirectory;
        Load();
    }

    private static string GuessName(string url)
    {
        try
        {
            var u = new Uri(url);
            return u.Host + (u.IsDefaultPort ? "" : ":" + u.Port) + (u.AbsolutePath == "/" ? "" : u.AbsolutePath);
        }
        catch { return url; }
    }

    private sealed class LegacyStreams
    {
        public List<string>? Urls { get; set; }
    }

    private void Load()
    {
        try
        {
            var p = Path.Combine(_base, "settings.json");
            if (File.Exists(p))
                Settings = JsonSerializer.Deserialize<Settings>(File.ReadAllText(p)) ?? new();
        }
        catch { }
        try
        {
            var p = Path.Combine(_base, "streams.json");
            if (File.Exists(p))
            {
                var raw = File.ReadAllText(p);
                try
                {
                    Streams = JsonSerializer.Deserialize<StreamsFile>(raw) ?? new();
                }
                catch
                {
                    // legacy format: plain URL strings
                    var old = JsonSerializer.Deserialize<LegacyStreams>(raw);
                    if (old?.Urls != null)
                        foreach (var u in old.Urls)
                            Streams.Urls.Add(new StreamEntry { Name = GuessName(u), Url = u });
                }
                Streams.Urls ??= new();
            }
        }
        catch { }
        try
        {
            var p = Path.Combine(_base, "servers.json");
            if (File.Exists(p))
                Servers = JsonSerializer.Deserialize<ServersFile>(File.ReadAllText(p)) ?? new();
        }
        catch { }
        Servers.Presets ??= new();
        if (Settings.SampleRate is not (44100 or 48000 or 96000)) Settings.SampleRate = 48000;
        if (Settings.SilenceSeconds < 10 || Settings.SilenceSeconds > 300) Settings.SilenceSeconds = 30;
        Settings.UrlStats ??= new();
    }

    public void SaveServers()
    {
        try { File.WriteAllText(Path.Combine(_base, "servers.json"), JsonSerializer.Serialize(Servers, Json)); }
        catch { }
    }

    public void SaveSettings()
    {
        try { File.WriteAllText(Path.Combine(_base, "settings.json"), JsonSerializer.Serialize(Settings, Json)); }
        catch { }
    }

    public void SaveStreams()
    {
        try { File.WriteAllText(Path.Combine(_base, "streams.json"), JsonSerializer.Serialize(Streams, Json)); }
        catch { }
    }
}
