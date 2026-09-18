using System.Collections.ObjectModel;
using System.IO;
using System.Text;
using Application = System.Windows.Application;

namespace AoIP_RX.Services;

/// <summary>Thread-safe live log: 500-line ring for UI + daily file in logs/. Portable.</summary>
public sealed class Logger
{
    private const int MaxLines = 500;
    private readonly object _gate = new();
    private readonly string _filePath;

    public ObservableCollection<string> Lines { get; } = new();

    public Logger()
    {
        var dir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "logs");
        Directory.CreateDirectory(dir);
        _filePath = Path.Combine(dir, $"AoIP-RX-{DateTime.Now:yyyyMMdd}.txt");
    }

    public void Log(string level, string message)
    {
        var line = $"{DateTime.Now:HH:mm:ss} [{level}] {message}";
        lock (_gate)
        {
            try { File.AppendAllText(_filePath, line + Environment.NewLine, Encoding.UTF8); } catch { }
        }
        var disp = Application.Current?.Dispatcher;
        if (disp == null || disp.CheckAccess()) Add(line);
        else disp.BeginInvoke(() => Add(line));
    }

    private void Add(string line)
    {
        Lines.Add(line);
        while (Lines.Count > MaxLines) Lines.RemoveAt(0);
    }

    public void Info(string m) => Log("INFO", m);
    public void Warn(string m) => Log("WARN", m);
    public void Error(string m) => Log("ERROR", m);

    public string ExportView(IEnumerable<string> visibleLines)
    {
        var path = Path.Combine(AppDomain.CurrentDomain.BaseDirectory,
            $"AoIP-RX-log-{DateTime.Now:yyyyMMdd-HHmm}.txt");
        File.WriteAllLines(path, visibleLines, Encoding.UTF8);
        return path;
    }
}
