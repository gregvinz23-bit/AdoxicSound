using System.Windows;
using System.Windows.Controls;
using Application = System.Windows.Application;
using ComboBox = System.Windows.Controls.ComboBox;

namespace AdoxicSound;

public partial class LogsWindow : Window
{
    private App App => (App)Application.Current;
    private readonly MainWindow _main;
    private string _logFilter = "All";

    public LogsWindow(MainWindow main)
    {
        _main = main;
        Owner = main;
        InitializeComponent();
        LogList.ItemsSource = App.Log.Lines;
        if (App.Log.Lines.Count > 0) LogList.ScrollIntoView(App.Log.Lines[^1]);
        Closed += (_, _) => _main.OnLogsClosed();
    }

    private void LogFilter_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (LogList == null) return;
        _logFilter = ((ComboBoxItem)LogFilter.SelectedItem)?.Content?.ToString() ?? "All";
        ApplyFilter();
    }

    private void ApplyFilter()
    {
        if (LogList == null) return;
        if (_logFilter == "All") LogList.ItemsSource = App.Log.Lines;
        else LogList.ItemsSource = App.Log.Lines.Where(l => l.Contains($"[{_logFilter}]")).ToList();
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
