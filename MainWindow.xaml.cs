using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using AoIP_RX.Services;
using Application = System.Windows.Application;
using Brush = System.Windows.Media.Brush;
using Brushes = System.Windows.Media.Brushes;
using Color = System.Windows.Media.Color;
using ComboBox = System.Windows.Controls.ComboBox;

namespace AoIP_RX;

/// <summary>Interaction logic for MainWindow.xaml</summary>
public partial class MainWindow : Window
{
    private App App => (App)Application.Current;
    private readonly ObservableCollection<string> _streams = new();
    private readonly DispatcherTimer _meterTimer = new();
    private string _logFilter = "All";
    private bool _loading = true;

    public MainWindow()
    {
        InitializeComponent();
        Width = App.Store.Settings.Width;
        Height = App.Store.Settings.Height;

        foreach (var u in App.Store.Streams.Urls)
            _streams.Add(u);
        StreamList.ItemsSource = _streams;
        LogList.ItemsSource = App.Log.Lines;

        try
        {
            var logo = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Logo.png");
            var uri = File.Exists(logo) ? new Uri(logo) : new Uri("pack://application:,,,/Logo.png");
            var img = new BitmapImage(uri);
            LogoImage.Source = img;
            Icon = img;
        }
        catch { }

        RefreshDevices();
        SetDeviceSelection(App.Store.Settings.OutputDeviceId);

        FastRadio.IsChecked = App.Store.Settings.FastMode;
        StableRadio.IsChecked = !App.Store.Settings.FastMode;
        RateSlider.Value = App.Store.Settings.SampleRate switch { 44100 => 0, 96000 => 2, _ => 1 };
        BalanceToggle.IsChecked = App.Store.Settings.AutoBalance;
        TrayToggle.IsChecked = App.Store.Settings.TrayOnClose;
        BootToggle.IsChecked = App.Store.Settings.StartOnBoot;

        if (!string.IsNullOrEmpty(App.Store.Settings.LastUrl))
        {
            UrlBox.Text = App.Store.Settings.LastUrl;
            SelectStream(App.Store.Settings.LastUrl);
        }

        App.Engine.StateChanged += () => Dispatcher.BeginInvoke(RefreshState);
        App.Engine.LevelsChanged += () => { };
        _meterTimer.Interval = TimeSpan.FromMilliseconds(66);
        _meterTimer.Tick += (_, _) => RefreshMeters();
        _meterTimer.Start();

        Closing += MainWindow_Closing;
        Closed += (_, _) => { _meterTimer.Stop(); };
        _loading = false;
        RefreshState();
        App.Log.Info("UI ready");
        if (!string.IsNullOrEmpty(App.PendingUrl))
        {
            UrlBox.Text = App.PendingUrl;
            App.Store.Settings.LastUrl = App.PendingUrl;
            App.Store.SaveSettings();
            App.Engine.Configure(App.Store.Settings.SampleRate, App.Store.Settings.FastMode);
            App.Engine.Play(App.PendingUrl);
        }
    }

    // ---------- state / meters ----------

    private void RefreshState()
    {
        var e = App.Engine;
        StatusText.Text = e.StatusText + (e.Attempt > 1 && e.State != EngineState.Playing ? $" (try {e.Attempt})" : "");
        DetailText.Text = e.DetailText;
        StatusDot.Fill = e.State switch
        {
            EngineState.Playing => Brushes.LimeGreen,
            EngineState.Buffering or EngineState.Connecting or EngineState.Reconnecting => Brushes.Gold,
            _ => Brushes.Gray
        };
        if (e.State == EngineState.Stopped && StatusText.Text == "Stopped") { }
        PlayStopButton.Content = e.State == EngineState.Stopped ? "▶  STREAM" : "■  STOP";
        PlayStopButton.Background = e.State == EngineState.Stopped
            ? (Brush)new SolidColorBrush(Color.FromRgb(0x2A, 0xA9, 0xE0)) : Brushes.Firebrick;
    }

    private void RefreshMeters()
    {
        var e = App.Engine;
        MeterL.Value = Math.Clamp(e.LevelL * 100, 0, 100);
        MeterR.Value = Math.Clamp(e.LevelR * 100, 0, 100);
        MeterL.Foreground = BrushFor(e.LevelL);
        MeterR.Foreground = BrushFor(e.LevelR);
        DbL.Text = DbText(e.LevelL);
        DbR.Text = DbText(e.LevelR);
    }

    private static Brush BrushFor(float l) =>
        l > 0.89 ? Brushes.Red : l > 0.5 ? Brushes.Gold : Brushes.LimeGreen;

    private static string DbText(float l) =>
        l <= 0.0001f ? "-∞ dB" : $"{20 * Math.Log10(l):0} dB";

    // ---------- stream tab ----------

    private void PlayStopButton_Click(object sender, RoutedEventArgs e)
    {
        if (App.Engine.State != EngineState.Stopped) { App.Engine.Stop(); return; }
        var url = UrlBox.Text.Trim();
        if (!url.StartsWith("mms://", StringComparison.OrdinalIgnoreCase) &&
            !url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) &&
            !url.StartsWith("https://", StringComparison.OrdinalIgnoreCase) &&
            !url.StartsWith("mmsh://", StringComparison.OrdinalIgnoreCase) &&
            !url.StartsWith("mmst://", StringComparison.OrdinalIgnoreCase))
        {
            App.Log.Error("URL must start with mms:// or http(s)://");
            return;
        }
        App.Store.Settings.LastUrl = url;
        App.Store.SaveSettings();
        App.Engine.Configure(App.Store.Settings.SampleRate, App.Store.Settings.FastMode);
        App.Engine.Play(url);
    }

    private void AddButton_Click(object sender, RoutedEventArgs e)
    {
        var url = UrlBox.Text.Trim();
        if (url.Length < 8 || (!url.Contains("://"))) { App.Log.Error("Enter a stream link first"); return; }
        if (_streams.Contains(url)) return;
        _streams.Add(url);
        App.Store.Streams.Urls.Add(url);
        App.Store.SaveStreams();
        App.Log.Info("Added: " + url);
    }

    private void RemoveButton_Click(object sender, RoutedEventArgs e)
    {
        if (StreamList.SelectedItem is not string s) return;
        _streams.Remove(s);
        App.Store.Streams.Urls.Remove(s);
        App.Store.SaveStreams();
    }

    private void StreamList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (StreamList.SelectedItem is string s) UrlBox.Text = s;
    }

    private void SelectStream(string url)
    {
        if (_streams.Contains(url)) StreamList.SelectedItem = url;
    }

    // ---------- devices / mode / rate ----------

    private List<(string Id, string Name, bool Default)> _devs = new();

    private void RefreshDevices()
    {
        _devs = StreamEngine.ListDevices();
        var names = _devs.Select(d => d.Name + (d.Default ? " (default)" : "")).ToList();
        if (names.Count == 0) names.Add("No output found — plug in a device");
        DeviceCombo.ItemsSource = names;
        SetDeviceCombo.ItemsSource = new List<string>(names);
        if (_devs.Count == 0) App.Log.Warn("No audio output devices found");
    }

    private void SetDeviceSelection(string? id)
    {
        var i = Math.Max(0, _devs.FindIndex(d => d.Id == id));
        if (_devs.Count == 0) { DeviceCombo.SelectedIndex = 0; SetDeviceCombo.SelectedIndex = 0; return; }
        DeviceCombo.SelectedIndex = i;
        SetDeviceCombo.SelectedIndex = i;
    }

    private void DeviceCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || _devs.Count == 0) return;
        var box = (ComboBox)sender;
        if (box.SelectedIndex < 0 || box.SelectedIndex >= _devs.Count) return;
        var id = _devs[box.SelectedIndex].Id;
        App.Store.Settings.OutputDeviceId = id;
        App.Store.SaveSettings();
        var other = box == DeviceCombo ? SetDeviceCombo : DeviceCombo;
        other.SelectedIndex = box.SelectedIndex;
        App.Log.Info("Output: " + _devs[box.SelectedIndex].Name + " (applies on next play)");
    }

    private void ModeRadio_Checked(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        App.Store.Settings.FastMode = FastRadio.IsChecked == true;
        App.Store.SaveSettings();
        App.Log.Info("Mode: " + (App.Store.Settings.FastMode ? "Fast 300ms" : "Stable 1000ms") + " (applies on next play)");
    }

    private void RateSlider_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        var rate = (int)RateSlider.Value switch { 0 => 44100, 2 => 96000, _ => 48000 };
        if (RateLabel != null) RateLabel.Text = $"{rate} Hz";
        if (_loading) return;
        App.Store.Settings.SampleRate = rate;
        App.Store.SaveSettings();
        App.Log.Info($"Sample rate: {rate} Hz (applies on next play)");
    }

    // ---------- settings toggles ----------

    private void BalanceToggle_Changed(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        App.Store.Settings.AutoBalance = BalanceToggle.IsChecked == true;
        App.Engine.AutoBalance = App.Store.Settings.AutoBalance;
        App.Store.SaveSettings();
        App.Log.Info("Auto balance " + (App.Store.Settings.AutoBalance ? "ON" : "OFF"));
    }

    private void TrayToggle_Changed(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        App.Store.Settings.TrayOnClose = TrayToggle.IsChecked == true;
        App.Store.SaveSettings();
    }

    private void BootToggle_Changed(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        App.Store.Settings.StartOnBoot = BootToggle.IsChecked == true;
        App.Store.SaveSettings();
        App.ApplyStartOnBoot();
        App.Log.Info("Start on boot " + (App.Store.Settings.StartOnBoot ? "ON" : "OFF"));
    }

    private void OpenFolderButton_Click(object sender, RoutedEventArgs e)
    {
        try { Process.Start("explorer.exe", AppDomain.CurrentDomain.BaseDirectory); } catch { }
    }

    private void ClearFavButton_Click(object sender, RoutedEventArgs e)
    {
        _streams.Clear();
        App.Store.Streams.Urls.Clear();
        App.Store.SaveStreams();
    }

    // ---------- logs ----------

    private void LogFilter_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (LogList == null) return;
        _logFilter = ((ComboBoxItem)LogFilter.SelectedItem)?.Content?.ToString() ?? "All";
        ApplyFilter();
    }

    private void ApplyFilter()
    {
        if (LogList == null) return;
        if (_logFilter == "All") { LogList.ItemsSource = App.Log.Lines; return; }
        LogList.ItemsSource = App.Log.Lines.Where(l => l.Contains($"[{_logFilter}]")).ToList();
    }

    private void ClearButton_Click(object sender, RoutedEventArgs e)
    {
        App.Log.Lines.Clear();
        ApplyFilter();
    }

    private void ExportButton_Click(object sender, RoutedEventArgs e)
    {
        var items = LogList.ItemsSource as System.Collections.IEnumerable;
        var lines = items?.Cast<object>().Select(o => o?.ToString() ?? "") ?? Enumerable.Empty<string>();
        var path = App.Log.ExportView(lines);
        App.Log.Info("Logs exported: " + path);
        ApplyFilter();
    }

    // ---------- about / updates ----------

    private async void UpdateButton_Click(object sender, RoutedEventArgs e)
    {
        UpdateText.Text = "Checking…";
        try
        {
            using var http = new HttpClient();
            http.DefaultRequestHeaders.UserAgent.ParseAdd("AoIP-RX/1.0");
            http.Timeout = TimeSpan.FromSeconds(10);
            var json = await http.GetStringAsync(
                "https://api.github.com/repos/gregvinz23-bit/AoIP-RX/releases/latest");
            var tag = JsonDocument.Parse(json).RootElement.GetProperty("tag_name").GetString() ?? "";
            var ver = tag.TrimStart('v', 'V');
            UpdateText.Text = ver == "1.0.0" || string.IsNullOrEmpty(ver)
                ? "You're up to date (v1.0.0)"
                : $"v{ver} available — see GitHub Releases";
            try
            {
                if (!string.IsNullOrEmpty(ver) && ver != "1.0.0")
                    Process.Start(new ProcessStartInfo("https://github.com/gregvinz23-bit/AoIP-RX/releases") { UseShellExecute = true });
            }
            catch { }
        }
        catch (Exception ex) { UpdateText.Text = "Check failed: " + ex.Message; }
    }

    // ---------- close / tray ----------

    private void MainWindow_Closing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        App.Store.Settings.Width = Width;
        App.Store.Settings.Height = Height;
        App.Store.SaveSettings();
        if (App.Store.Settings.TrayOnClose)
        {
            e.Cancel = true;
            Hide();
            App.Log.Info("Minimized to tray (right-click tray icon to exit)");
        }
        else App.Engine.Stop();
    }
}
