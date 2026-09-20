using System.Windows;
using System.Windows.Controls;
using Application = System.Windows.Application;
using ComboBox = System.Windows.Controls.ComboBox;

namespace AdoxicSound;

public partial class LogsWindow : Window
{
    private App App => (App)Application.Current;
    private readonly MainWindow _main;
    private readonly string _source; // RX or TX; APP lines show in both
    private string _logFilter = "All";

    public LogsWindow(MainWindow main, string source)
    {
        _main = main;
        _source = source;
        Owner = main;
        InitializeComponent();
        Title = source == "TX" ? "Logs — Send" : "Logs — Stream";
        ApplyFilter();
        if (LogList.Items.Count > 0) LogList.ScrollIntoView(LogList.Items[^1]);
        App.Log.Lines.CollectionChanged += OnLinesChanged;
        Closed += (_, _) =>
        {
            App.Log.Lines.CollectionChanged -= OnLinesChanged;
            _main.OnLogsClosed(_source);
        };
    }

    private void OnLinesChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e) =>
        Dispatcher.BeginInvoke(() =>
        {
            ApplyFilter();
            if (AutoScrollBox.IsChecked == true && LogList.Items.Count > 0)
                LogList.ScrollIntoView(LogList.Items[^1]);
        });

    private void LogFilter_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (LogList == null) return;
        _logFilter = ((ComboBoxItem)LogFilter.SelectedItem)?.Content?.ToString() ?? "All";
        ApplyFilter();
    }

    private bool SourceMatch(string line) =>
        line.Contains($"[{_source}]") || line.Contains("[APP]") || (!line.Contains("[RX]") && !line.Contains("[TX]"));

    private void ApplyFilter()
    {
        if (LogList == null) return;
        var lines = App.Log.Lines.Where(SourceMatch);
        if (_logFilter != "All") lines = lines.Where(l => l.Contains($"[{_logFilter}]"));
        LogList.ItemsSource = lines.ToList();
        if (AutoScrollBox.IsChecked == true && LogList.Items.Count > 0)
            LogList.ScrollIntoView(LogList.Items[^1]);
    }

    private void ClearButton_Click(object sender, RoutedEventArgs e)
    {
        App.Log.Lines.Clear();
        ApplyFilter();
    }

    private void ExportButton_Click(object sender, RoutedEventArgs e)
    {
        var items = LogList.ItemsSource as System.Collections.IEnumerable;
        var lines = (items?.Cast<object>().Select(o => o?.ToString() ?? "") ?? Enumerable.Empty<string>()).ToList();
        var head = new List<string>
        {
            $"Adoxic Sound log export {DateTime.Now:yyyy-MM-dd HH:mm:ss}",
            $"Stream: {App.Engine.CurrentUrl}",
            $"Session: {App.Engine.StatsLine(out var life)}",
        };
        if (!string.IsNullOrEmpty(life)) head.Add(life);
        head.Add(new string('-', 40));
        var path = App.Log.ExportView(head.Concat(lines));
        App.Log.Info("Logs exported: " + path);
        ApplyFilter();
    }
}
