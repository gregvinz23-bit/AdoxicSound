using System.IO;
using System.Windows;
using System.Windows.Controls;
using Application = System.Windows.Application;

namespace AdoxicSound;

public partial class EditorWindow : Window
{
    private App App => (App)Application.Current;
    private readonly MainWindow _main;

    public EditorWindow(MainWindow main)
    {
        _main = main;
        Owner = main;
        InitializeComponent();
        RefreshFiles();
        Closed += (_, _) => _main.OnEditorClosed();
    }

    private void RefreshFiles()
    {
        EdFiles.ItemsSource = App.Rec.TodayFiles().Select(f => f.Name).ToList();
    }

    private void EdFiles_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (EdFiles.SelectedItem is not string name) return;
        var folder = string.IsNullOrWhiteSpace(App.Store.Settings.RecFolder)
            ? Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "records")
            : App.Store.Settings.RecFolder;
        var path = Path.Combine(folder, name);
        try
        {
            var fi = new FileInfo(path);
            EdFmt.Text = $"{fi.Extension.TrimStart('.').ToUpper()} · {fi.Length / 1048576.0:0.0}MB";
            EdSel.Text = $"Sel --:--–--:--   {name}";
        }
        catch { }
    }

    private void EdPlay_Click(object sender, RoutedEventArgs e) { }
    private void EdStop_Click(object sender, RoutedEventArgs e) { }
    private void EdHome_Click(object sender, RoutedEventArgs e) { }
    private void EdTrim_Click(object sender, RoutedEventArgs e) { }
    private void EdSave_Click(object sender, RoutedEventArgs e) { }
}
