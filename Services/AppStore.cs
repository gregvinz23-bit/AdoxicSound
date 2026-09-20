using System.IO;
using System.Text.Json;

namespace AoIP_RX.Services;

public sealed class Settings
{
    public string? OutputDeviceId { get; set; }
    public int SampleRate { get; set; } = 48000;
    public bool FastMode { get; set; } = true;
    public bool TrayOnClose { get; set; } = true;
    public bool StartOnBoot { get; set; } = false;
    public bool AlarmEnabled { get; set; } = true;
    public int SilenceSeconds { get; set; } = 30;
    public bool RestoreLast { get; set; } = true;
    public string? LastUrl { get; set; }
    public double Width { get; set; } = 760;
    public double Height { get; set; } = 540;
    public double SetWinX { get; set; } = -1;
    public double SetWinY { get; set; } = -1;
    public double LogWinX { get; set; } = -1;
    public double LogWinY { get; set; } = -1;
    public Dictionary<string, UrlStatRecord> UrlStats { get; set; } = new();
}

public sealed class UrlStatRecord
{
    public long Drops { get; set; }
    public long HealthySec { get; set; }
}

public sealed class StreamsFile
{
    public List<string> Urls { get; set; } = new();
}

/// <summary>Portable store: streams.json + settings.json next to the exe.</summary>
public sealed class AppStore
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };
    private readonly string _base;

    public Settings Settings { get; private set; } = new();
    public StreamsFile Streams { get; private set; } = new();

    public AppStore()
    {
        _base = AppDomain.CurrentDomain.BaseDirectory;
        Load();
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
                Streams = JsonSerializer.Deserialize<StreamsFile>(File.ReadAllText(p)) ?? new();
        }
        catch { }
        if (Settings.SampleRate is not (44100 or 48000 or 96000)) Settings.SampleRate = 48000;
        if (Settings.SilenceSeconds < 10 || Settings.SilenceSeconds > 300) Settings.SilenceSeconds = 30;
        Settings.UrlStats ??= new();
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
