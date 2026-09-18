using System.Drawing;
using System.IO;
using System.Windows;
using AoIP_RX.Services;
using Microsoft.Win32;
using WinForms = System.Windows.Forms;

namespace AoIP_RX;

/// <summary>
/// Interaction logic for App.xaml. Owns portable store, log, engine, tray icon.
/// </summary>
public partial class App : System.Windows.Application
{
    public AppStore Store { get; private set; } = null!;
    public Logger Log { get; private set; } = null!;
    public StreamEngine Engine { get; private set; } = null!;
    public Icon? TrayIcon { get; private set; }
    public string PendingUrl { get; private set; } = "";

    private WinForms.NotifyIcon? _tray;
    private bool _trayRegistered;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        Store = new AppStore();
        Log = new Logger();
        Engine = new StreamEngine(Log);
        Engine.Configure(Store.Settings.SampleRate, Store.Settings.FastMode);
        Engine.AutoBalance = Store.Settings.AutoBalance;
        ApplyStartOnBoot();
        SetupTray();
        PendingUrl = e.Args.FirstOrDefault(a => a.Contains("://")) ?? "";
        Log.Info("AoIP RX v1.0 started");
    }

    public void SetupTray()
    {
        if (_trayRegistered) return;
        _trayRegistered = true;
        try
        {
            Bitmap? bmp = null;
            var logo = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Logo.png");
            if (File.Exists(logo)) bmp = new Bitmap(logo);
            else
            {
                var s = System.Windows.Application.GetResourceStream(
                    new Uri("pack://application:,,,/Logo.png"))?.Stream;
                if (s != null) { bmp = new Bitmap(s); s.Dispose(); }
            }
            if (bmp != null)
            {
                TrayIcon = Icon.FromHandle(bmp.GetHicon());
                bmp.Dispose();
            }
        }
        catch { }
        _tray = new WinForms.NotifyIcon
        {
            Text = "AoIP RX",
            Icon = TrayIcon ?? SystemIcons.Application,
            Visible = true
        };
        var menu = new WinForms.ContextMenuStrip();
        menu.Items.Add("Open", null, (_, _) => Restore());
        menu.Items.Add("Stop", null, (_, _) => Engine.Stop());
        menu.Items.Add("Exit", null, (_, _) => Shutdown());
        _tray.ContextMenuStrip = menu;
        _tray.DoubleClick += (_, _) => Restore();
    }

    public void Restore()
    {
        var w = MainWindow;
        if (w == null) return;
        w.Show();
        if (w.WindowState == WindowState.Minimized) w.WindowState = WindowState.Normal;
        w.Activate();
    }

    public void ApplyStartOnBoot()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(
                @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run", writable: true);
            if (key == null) return;
            if (Store.Settings.StartOnBoot)
                key.SetValue("AoIP-RX", $"\"{Environment.ProcessPath}\"");
            else if (key.GetValue("AoIP-RX") != null)
                key.DeleteValue("AoIP-RX");
        }
        catch { }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        try { Store.SaveSettings(); } catch { }
        try { if (_tray != null) { _tray.Visible = false; _tray.Dispose(); } } catch { }
        try { Engine.Dispose(); } catch { }
        base.OnExit(e);
    }
}


