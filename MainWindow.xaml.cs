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

namespace AoIP_RX;

/// <summary>Interaction logic for MainWindow.xaml</summary>
public partial class MainWindow : Window
{
    private App App => (App)Application.Current;
    private readonly ObservableCollection<string> _streams = new();
    private readonly DispatcherTimer _meterTimer = new();
    private readonly DispatcherTimer _slowTimer = new();
    private float _dispL, _dispR;

    public MainWindow()
    {
        InitializeComponent();

        foreach (var u in App.Store.Streams.Urls)
            _streams.Add(u);
        StreamList.ItemsSource = _streams;

        try
        {
            using var ico = System.Drawing.Icon.ExtractAssociatedIcon(Environment.ProcessPath!);
            if (ico != null)
            {
                var src = System.Windows.Interop.Imaging.CreateBitmapSourceFromHIcon(
                    ico.Handle, System.Windows.Int32Rect.Empty,
                    System.Windows.Media.Imaging.BitmapSizeOptions.FromEmptyOptions());
                src.Freeze();
                Icon = src;
            }
        }
        catch (Exception ex) { App.Log.Warn("Window icon failed: " + ex.Message); }
        try
        {
            var logo = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Logo.png");
            var uri = File.Exists(logo) ? new Uri(logo) : new Uri("pack://application:,,,/Logo.png");
            LogoImage.Source = new BitmapImage(uri);
        }
        catch (Exception ex) { App.Log.Warn("About logo failed: " + ex.Message); }

        if (!string.IsNullOrEmpty(App.Store.Settings.LastUrl))
        {
            UrlBox.Text = App.Store.Settings.LastUrl;
            SelectStream(App.Store.Settings.LastUrl);
        }

        App.Engine.StateChanged += () => Dispatcher.BeginInvoke(RefreshState);
        _meterTimer.Interval = TimeSpan.FromMilliseconds(25);
        _meterTimer.Tick += (_, _) => RefreshMeters();
        _meterTimer.Start();
        _slowTimer.Interval = TimeSpan.FromSeconds(1);
        _slowTimer.Tick += (_, _) => { RefreshState(); RefreshStats(); };
        _slowTimer.Start();

        App.Watcher.DeviceAdded += () => Dispatcher.BeginInvoke(() => App.Log.Info("Audio device added"));
        App.Watcher.DeviceRemoved += id => Dispatcher.BeginInvoke(() => OnDeviceLost(id));
        App.Watcher.DevicesChanged += () => Dispatcher.BeginInvoke(() => _settingsWin?.RefreshDevices());

        Closing += MainWindow_Closing;
        Closed += (_, _) => { _meterTimer.Stop(); _slowTimer.Stop(); };
        VersionText.Text = "AoIP RX v" + CurrentVersion;
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
        var (pl, pr) = App.Engine.ConsumePeaks();
        // time-based ballistics: instant attack, ~0.8s smooth release — same on any PC
        _dispL = Math.Max(pl, _dispL * 0.94f);
        _dispR = Math.Max(pr, _dispR * 0.94f);
        MeterL.Value = Math.Clamp(_dispL * 100, 0, 100);
        MeterR.Value = Math.Clamp(_dispR * 100, 0, 100);
        MeterL.Foreground = BrushFor(_dispL);
        MeterR.Foreground = BrushFor(_dispR);
        DbL.Text = DbText(_dispL);
        DbR.Text = DbText(_dispR);
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

    // ---------- child windows ----------

    private SettingsWindow? _settingsWin;
    private LogsWindow? _logsWin;

    private void SettingsButton_Click(object sender, RoutedEventArgs e) => OpenSettings();
    private void LogsButton_Click(object sender, RoutedEventArgs e) => OpenLogs();

    public void OpenSettings()
    {
        if (_settingsWin == null)
        {
            _settingsWin = new SettingsWindow(this);
            Place(_settingsWin, App.Store.Settings.SetWinX, App.Store.Settings.SetWinY);
        }
        _settingsWin.Show();
        _settingsWin.Activate();
    }

    public void OpenLogs()
    {
        if (_logsWin == null)
        {
            _logsWin = new LogsWindow(this);
            Place(_logsWin, App.Store.Settings.LogWinX, App.Store.Settings.LogWinY);
        }
        _logsWin.Show();
        _logsWin.Activate();
    }

    private void Place(Window w, double x, double y)
    {
        w.WindowStartupLocation = WindowStartupLocation.CenterOwner;
        w.ShowInTaskbar = false;
        if (x >= 0 && y >= 0)
        {
            w.WindowStartupLocation = WindowStartupLocation.Manual;
            w.Left = x;
            w.Top = y;
        }
    }

    public void OnSettingsClosed()
    {
        if (_settingsWin != null)
        {
            App.Store.Settings.SetWinX = _settingsWin.Left;
            App.Store.Settings.SetWinY = _settingsWin.Top;
            App.Store.SaveSettings();
            _settingsWin = null;
        }
    }

    public void OnLogsClosed()
    {
        if (_logsWin != null)
        {
            App.Store.Settings.LogWinX = _logsWin.Left;
            App.Store.Settings.LogWinY = _logsWin.Top;
            App.Store.SaveSettings();
            _logsWin = null;
        }
    }

    // ---------- devices ----------

    public void OnDeviceLost(string id)
    {
        App.Log.Warn("Audio device removed");
        App.Engine.OnOutputLost(id);
        _settingsWin?.RefreshDevices();
        RefreshState();
    }

    private void RefreshStats()
    {
        StatsText.Text = App.Engine.StatsLine(out var life);
        LifeText.Text = life;
        if (App.Engine.DownSince is DateTime since)
            StatsText.Text += $"  |  DOWN {DateTime.UtcNow - since:hh\\:mm\\:ss}";
    }

    private void StatsReset_Click(object sender, RoutedEventArgs e)
    {
        var url = App.Engine.CurrentUrl;
        if (!string.IsNullOrEmpty(url) && App.Store.Settings.UrlStats.Remove(url))
        {
            App.Store.SaveSettings();
            App.Log.Info("Lifetime stats cleared for this stream");
            RefreshStats();
        }
    }

    // ---------- about / updates ----------

    private static string CurrentVersion =>
        System.Reflection.Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "1.1.0";

    private async void UpdateButton_Click(object sender, RoutedEventArgs e)
    {
        UpdateText.Text = "Checking…";
        var current = CurrentVersion;
        try
        {
            using var http = new HttpClient();
            http.DefaultRequestHeaders.UserAgent.ParseAdd("AoIP-RX/" + current);
            http.Timeout = TimeSpan.FromSeconds(10);
            var json = await http.GetStringAsync(
                "https://api.github.com/repos/gregvinz23-bit/AoIP-RX/releases/latest");
            var tag = JsonDocument.Parse(json).RootElement.GetProperty("tag_name").GetString() ?? "";
            var ver = tag.TrimStart('v', 'V');
            if (!Version.TryParse(ver, out var latest) || !Version.TryParse(current, out var mine))
                UpdateText.Text = "Check failed: bad version data";
            else if (latest <= mine) UpdateText.Text = $"You're up to date (v{current})";
            else
            {
                UpdateText.Text = $"v{ver} available (you have v{current}) — opening download page…";
                App.Log.Info($"Update available: v{ver}");
                try
                {
                    Process.Start(new ProcessStartInfo("https://github.com/gregvinz23-bit/AoIP-RX/releases") { UseShellExecute = true });
                }
                catch { }
            }
        }
        catch (Exception ex) { UpdateText.Text = "Check failed: " + ex.Message; }
    }

    // ---------- close / tray ----------

    private void MainWindow_Closing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (App.Store.Settings.TrayOnClose)
        {
            e.Cancel = true;
            Hide();
            App.Log.Info("Minimized to tray (right-click tray icon to exit)");
        }
        else App.Engine.Stop();
    }
}
