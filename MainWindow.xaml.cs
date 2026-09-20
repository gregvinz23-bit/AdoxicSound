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
using AdoxicSound.Services;
using Application = System.Windows.Application;
using Brush = System.Windows.Media.Brush;
using Brushes = System.Windows.Media.Brushes;
using Color = System.Windows.Media.Color;

namespace AdoxicSound;

/// <summary>Interaction logic for MainWindow.xaml</summary>
public partial class MainWindow : Window
{
    private App App => (App)Application.Current;
    private readonly ObservableCollection<string> _streams = new();
    private readonly DispatcherTimer _meterTimer = new();
    private readonly DispatcherTimer _slowTimer = new();
    private float _dispL, _dispR;
    private float _sendDisp;
    private bool _sendSilenceAlarmed;

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
            var logo = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Adoxic_Sound_Logo.png");
            var uri = File.Exists(logo) ? new Uri(logo) : new Uri("pack://application:,,,/Adoxic_Sound_Logo.png");
            LogoImage.Source = new BitmapImage(uri);
        }
        catch (Exception ex) { App.Log.Warn("About logo failed: " + ex.Message); }

        if (!string.IsNullOrEmpty(App.Store.Settings.LastUrl))
        {
            UrlBox.Text = App.Store.Settings.LastUrl;
            SelectStream(App.Store.Settings.LastUrl);
        }

        App.Engine.StateChanged += () => Dispatcher.BeginInvoke(RefreshState);
        App.Send.StateChanged += () => Dispatcher.BeginInvoke(RefreshSend);
        InitSendTab();
        if (App.Store.Settings.GoLiveOnBoot)
        {
            var sp = SendCfg();
            if (!string.IsNullOrWhiteSpace(sp.Host) && !string.IsNullOrEmpty(DecodeB64(sp.PassB64)))
            {
                App.Send.Version = CurrentVersion;
                App.Send.Start(ClonePreset(sp));
                App.Log.Info("Auto go-live on startup");
            }
            else App.Log.Warn("Go-live on startup skipped: server not configured");
        }
        _meterTimer.Interval = TimeSpan.FromMilliseconds(25);
        _meterTimer.Tick += (_, _) => RefreshMeters();
        _meterTimer.Start();
        _slowTimer.Interval = TimeSpan.FromSeconds(1);
        _slowTimer.Tick += (_, _) => { RefreshState(); RefreshStats(); RefreshSend(); CheckSendSilence(); };
        _slowTimer.Start();

        App.Watcher.DeviceAdded += () => Dispatcher.BeginInvoke(() => App.Log.Info("Audio device added"));
        App.Watcher.DeviceRemoved += id => Dispatcher.BeginInvoke(() => OnDeviceLost(id));
        App.Watcher.DevicesChanged += () => Dispatcher.BeginInvoke(() => { _settingsWin?.RefreshDevices(); _settingsWin?.RefreshInputs(); });

        Closing += MainWindow_Closing;
        Closed += (_, _) => { _meterTimer.Stop(); _slowTimer.Stop(); };
        VersionText.Text = "Adoxic Sound v" + CurrentVersion;
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
        var (mp, _) = App.Send.ConsumeMic();
        _sendDisp = Math.Max(mp, _sendDisp * 0.94f);
        SendMeter.Value = Math.Clamp(_sendDisp * 100, 0, 100);
        SendMeter.Foreground = BrushFor(_sendDisp);
        SendDb.Text = DbText(_sendDisp);
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
    private LogsWindow? _rxLogsWin;
    private LogsWindow? _txLogsWin;

    private void SettingsButton_Click(object sender, RoutedEventArgs e) => OpenSettings();
    private void LogsButton_Click(object sender, RoutedEventArgs e) => OpenRxLogs();
    private void SendLogsButton_Click(object sender, RoutedEventArgs e) => OpenTxLogs();

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

    public void OpenRxLogs() => OpenLogsWin("RX");
    public void OpenTxLogs() => OpenLogsWin("TX");

    private void OpenLogsWin(string source)
    {
        var win = source == "TX" ? _txLogsWin : _rxLogsWin;
        if (win == null)
        {
            win = new LogsWindow(this, source);
            if (source == "TX") _txLogsWin = win; else _rxLogsWin = win;
            var s = App.Store.Settings;
            Place(win, source == "TX" ? s.TxLogWinX : s.LogWinX,
                       source == "TX" ? s.TxLogWinY : s.LogWinY);
        }
        win.Show();
        win.Activate();
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

    public void OnLogsClosed(string source)
    {
        var win = source == "TX" ? _txLogsWin : _rxLogsWin;
        if (win != null)
        {
            var s = App.Store.Settings;
            if (source == "TX") { s.TxLogWinX = win.Left; s.TxLogWinY = win.Top; _txLogsWin = null; }
            else { s.LogWinX = win.Left; s.LogWinY = win.Top; _rxLogsWin = null; }
            App.Store.SaveSettings();
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

    // ---------- send tab (single server config, auto-saved) ----------

    private bool _sendLoading = true;

    private SendPreset SendCfg()
    {
        var s = App.Store.Servers;
        var p = s.Presets.FirstOrDefault();
        if (p == null)
        {
            p = new SendPreset { Name = "Server" };
            s.Presets.Add(p);
            App.Store.SaveServers();
        }
        return p;
    }

    private void InitSendTab()
    {
        var p = SendCfg();
        _sendLoading = true;
        SendTypeBox.SelectedIndex = (int)p.Type;
        SendHost.Text = p.Host;
        SendPort.Text = p.Port.ToString();
        SendMount.Text = p.Mount;
        SendUser.Text = p.User;
        SendPass.Password = DecodeB64(p.PassB64);
        _sendLoading = false;
        RefreshSend();
    }

    private static string DecodeB64(string b)
    {
        try { return System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(b)); }
        catch { return b; }
    }

    private void SendField_Changed(object sender, RoutedEventArgs e)
    {
        if (_sendLoading) return;
        var p = SendCfg();
        p.Type = (SendServerType)Math.Max(0, SendTypeBox.SelectedIndex);
        p.Host = SendHost.Text.Trim();
        if (int.TryParse(SendPort.Text.Trim(), out var port)) p.Port = Math.Clamp(port, 1, 65535);
        p.Mount = SendMount.Text.Trim();
        p.User = SendUser.Text.Trim();
        p.PassB64 = SendEngine.Encode(SendPass.Password);
        App.Store.SaveServers();
    }

    private void GoLiveButton_Click(object sender, RoutedEventArgs e)
    {
        if (App.Send.State != SendState.Stopped) { App.Send.Stop(); return; }
        SendField_Changed(sender, e);
        var p = SendCfg();
        if (string.IsNullOrWhiteSpace(p.Host)) { App.Log.Error("Enter the server host first"); return; }
        if (string.IsNullOrEmpty(DecodeB64(p.PassB64))) { App.Log.Error("Enter the server password first"); return; }
        App.Send.Version = CurrentVersion;
        App.Send.Start(ClonePreset(p));
    }

    private static SendPreset ClonePreset(SendPreset p) => new()
    {
        Name = p.Name, Type = p.Type, Host = p.Host, Port = p.Port, Mount = p.Mount,
        User = p.User, PassB64 = p.PassB64, Bitrate = p.Bitrate, InputDeviceId = p.InputDeviceId
    };

    private void RefreshSend()
    {
        var s = App.Send;
        SendStatus.Text = s.StatusText + (s.Attempt > 1 && s.State != SendState.Live ? $" (try {s.Attempt})" : "");
        SendDetail.Text = s.State == SendState.Live
            ? $"{s.Config.Type} {s.Config.Host}:{s.Config.Port}{s.Config.Mount} · {s.Config.Bitrate}k"
            : "";
        SendDot.Fill = s.State switch
        {
            SendState.Live => Brushes.LimeGreen,
            SendState.Connecting or SendState.Reconnecting => Brushes.Gold,
            _ => Brushes.Gray
        };
        GoLiveButton.Content = s.State == SendState.Stopped ? "●  GO LIVE" : "■  STOP";
        GoLiveButton.Background = s.State == SendState.Stopped
            ? (Brush)new SolidColorBrush(Color.FromRgb(0x2A, 0xA9, 0xE0)) : Brushes.Firebrick;
        var up = s.LiveSince is DateTime t ? DateTime.UtcNow - t : TimeSpan.Zero;
        SendStats.Text = s.State == SendState.Stopped && s.BytesSent == 0 ? ""
            : $"Up {up:hh\\:mm\\:ss} · {s.BytesSent / 1024}KB sent · Drops {s.Drops}";
    }

    private void CheckSendSilence()
    {
        if (App.Send.State != SendState.Live || !App.Store.Settings.AlarmEnabled) return;
        var quietFor = (DateTime.UtcNow - App.Send.LastAudibleUtc).TotalSeconds;
        if (quietFor < App.Store.Settings.SilenceSeconds) { _sendSilenceAlarmed = false; return; }
        if (_sendSilenceAlarmed) return;
        _sendSilenceAlarmed = true;
        App.Log.Error($"Mic silent {(int)quietFor}s — nothing going out");
        App.NotifyBalloon("Adoxic Sound — mic silent", $"No input for {(int)quietFor}s");
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
            http.DefaultRequestHeaders.UserAgent.ParseAdd("AdoxicSound/" + current);
            http.Timeout = TimeSpan.FromSeconds(10);
            var json = await http.GetStringAsync(
                "https://api.github.com/repos/gregvinz23-bit/AdoxicSound/releases/latest");
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
                    Process.Start(new ProcessStartInfo("https://github.com/gregvinz23-bit/AdoxicSound/releases") { UseShellExecute = true });
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
