using System.Windows;
using System.Windows.Controls;
using AdoxicSound.Services;
using Application = System.Windows.Application;
using ComboBox = System.Windows.Controls.ComboBox;

namespace AdoxicSound;

public partial class SettingsWindow : Window
{
    private App App => (App)Application.Current;
    private readonly MainWindow _main;
    private bool _loading = true;
    private List<(string Id, string Name, bool Default)> _devs = new();
    private List<(string Id, string Name, bool Loopback)> _inputs = new();
    private static readonly int[] SilenceOptions = { 10, 15, 30, 60, 120, 300 };
    private static readonly int[] SendRates = { 64, 96, 128, 160, 320 };
    private static readonly int[] SendCuts = { 0, 5, 15, 30, 60 };
    private static readonly int[] SendRetries = { 4, 10, 30 };

    public SettingsWindow(MainWindow main)
    {
        _main = main;
        Owner = main;
        InitializeComponent();
        RefreshDevices();
        SetDeviceSelection(App.Store.Settings.OutputDeviceId);
        RateCombo.SelectedIndex = App.Store.Settings.SampleRate switch { 44100 => 0, 96000 => 2, _ => 1 };
        ModeToggle.IsChecked = !App.Store.Settings.FastMode;
        AlarmToggle.IsChecked = App.Store.Settings.AlarmEnabled;
        SilenceCombo.SelectedIndex = ClosestSilence(App.Store.Settings.SilenceSeconds);
        _inputs = new List<(string Id, string Name, bool Loopback)>();
        RefreshInputs();
        var sp = SendCfg();
        SendRateBox.SelectedIndex = Math.Max(0, Array.IndexOf(SendRates, sp.Bitrate));
        if (SendRateBox.SelectedIndex < 0) SendRateBox.SelectedIndex = 2;
        SendChBox.SelectedIndex = sp.Channels switch { 1 => 1, 2 => 2, _ => 0 };
        SendCutBox.SelectedIndex = Math.Max(0, Array.IndexOf(SendCuts, sp.CutoffMin));
        SendRetryBox.SelectedIndex = Math.Max(0, Array.IndexOf(SendRetries, sp.ReconnectSec));
        GoLiveBootToggle.IsChecked = App.Store.Settings.GoLiveOnBoot;
        TrayToggle.IsChecked = App.Store.Settings.TrayOnClose;
        BootToggle.IsChecked = App.Store.Settings.StartOnBoot;
        App.Watcher.DevicesChanged += Watcher_Devices;
        App.Watcher.DeviceRemoved += Watcher_Removed;
        Closed += (_, _) =>
        {
            App.Watcher.DevicesChanged -= Watcher_Devices;
            App.Watcher.DeviceRemoved -= Watcher_Removed;
            _main.OnSettingsClosed();
        };
        _loading = false;
    }

    private void Watcher_Devices() => Dispatcher.BeginInvoke(() => { RefreshDevices(); RefreshInputs(); });
    private void Watcher_Removed(string id) => Dispatcher.BeginInvoke(() => _main.OnDeviceLost(id));

    public void RefreshInputs()
    {
        var keep = SendCfg().InputDeviceId;
        var keepLoop = SendCfg().InputLoopback;
        _inputs = SendEngine.ListInputs();
        SendInputBox.ItemsSource = _inputs.Select(d => d.Name).ToList();
        if (_inputs.Count == 0) SendInputBox.ItemsSource = new List<string> { "No input found" };
        var ii = _inputs.FindIndex(d => d.Id == keep && d.Loopback == keepLoop);
        SendInputBox.SelectedIndex = Math.Max(0, ii);
        if (_inputs.Count == 0) SendInputBox.SelectedIndex = 0;
    }

    public void RefreshDevices()
    {
        var keep = App.Store.Settings.OutputDeviceId;
        _devs = StreamEngine.ListDevices();
        var names = _devs.Select(d => d.Name + (d.Default ? " (default)" : "")).ToList();
        if (names.Count == 0) names.Add("No output found — plug in a device");
        SetDeviceCombo.ItemsSource = names;
        var i = _devs.FindIndex(d => d.Id == keep);
        if (i < 0) i = Math.Max(0, _devs.FindIndex(d => d.Default));
        if (_devs.Count == 0) i = 0;
        SetDeviceCombo.SelectedIndex = i;
        if (_devs.Count == 0) App.Log.Warn("No audio output devices found");
    }

    private void SetDeviceSelection(string? id)
    {
        if (_devs.Count == 0) { SetDeviceCombo.SelectedIndex = 0; return; }
        SetDeviceCombo.SelectedIndex = Math.Max(0, _devs.FindIndex(d => d.Id == id));
    }

    private void DeviceCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || _devs.Count == 0) return;
        var box = (ComboBox)sender;
        if (box.SelectedIndex < 0 || box.SelectedIndex >= _devs.Count) return;
        App.Store.Settings.OutputDeviceId = _devs[box.SelectedIndex].Id;
        App.Store.SaveSettings();
        App.Log.Info("Output: " + _devs[box.SelectedIndex].Name + " (applies on next play)");
    }

    private void RateCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || RateCombo.SelectedIndex < 0) return;
        var rate = RateCombo.SelectedIndex switch { 0 => 44100, 2 => 96000, _ => 48000 };
        App.Store.Settings.SampleRate = rate;
        App.Store.SaveSettings();
        App.Log.Info($"Sample rate: {rate} Hz (applies on next play)");
    }

    private void ModeToggle_Changed(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        App.Store.Settings.FastMode = ModeToggle.IsChecked != true;
        App.Store.SaveSettings();
        App.Log.Info("Mode: " + (App.Store.Settings.FastMode ? "Fast start 300ms" : "Stable 1000ms") + " (applies on next play)");
    }

    private void AlarmToggle_Changed(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        App.Store.Settings.AlarmEnabled = AlarmToggle.IsChecked == true;
        App.Store.SaveSettings();
        App.Log.Info("Alarms " + (App.Store.Settings.AlarmEnabled ? "ON" : "OFF"));
    }

    private static int ClosestSilence(int s)
    {
        var best = 0;
        for (var i = 1; i < SilenceOptions.Length; i++)
            if (Math.Abs(SilenceOptions[i] - s) < Math.Abs(SilenceOptions[best] - s)) best = i;
        return best;
    }

    private void SilenceCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || SilenceCombo.SelectedIndex < 0) return;
        App.Store.Settings.SilenceSeconds = SilenceOptions[SilenceCombo.SelectedIndex];
        App.Store.SaveSettings();
        App.Log.Info($"Silence alarm after {App.Store.Settings.SilenceSeconds}s");
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

    private void SendInput_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || SendInputBox.SelectedIndex < 0) return;
        if (SendInputBox.SelectedIndex < _inputs.Count)
        {
            SendCfg().InputDeviceId = _inputs[SendInputBox.SelectedIndex].Id;
            SendCfg().InputLoopback = _inputs[SendInputBox.SelectedIndex].Loopback;
        }
        App.Store.SaveServers();
    }

    private void SendRate_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || SendRateBox.SelectedIndex < 0) return;
        SendCfg().Bitrate = SendRates[SendRateBox.SelectedIndex];
        App.Store.SaveServers();
        App.Log.Info($"Send bitrate: {SendCfg().Bitrate} kbps (applies on next go-live)");
    }

    private void SendCh_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || SendChBox.SelectedIndex < 0) return;
        SendCfg().Channels = SendChBox.SelectedIndex;
        App.Store.SaveServers();
        App.Log.Info("Send channels: " + (SendChBox.SelectedIndex switch { 1 => "mono", 2 => "stereo", _ => "auto" }));
    }

    private void SendCut_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || SendCutBox.SelectedIndex < 0) return;
        SendCfg().CutoffMin = SendCuts[SendCutBox.SelectedIndex];
        App.Store.SaveServers();
    }

    private void SendRetry_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || SendRetryBox.SelectedIndex < 0) return;
        SendCfg().ReconnectSec = SendRetries[SendRetryBox.SelectedIndex];
        App.Store.SaveServers();
    }

    private void GoLiveBoot_Changed(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        App.Store.Settings.GoLiveOnBoot = GoLiveBootToggle.IsChecked == true;
        App.Store.SaveSettings();
        App.Log.Info("Go live on startup " + (App.Store.Settings.GoLiveOnBoot ? "ON" : "OFF"));
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
