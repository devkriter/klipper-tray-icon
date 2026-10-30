using System;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using KlipperTrayIcon.UI;

namespace KlipperTrayIcon;

public partial class App : Application
{
    private Mutex? _singleInstance;
    private TrayController? _tray;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        DispatcherUnhandledException += OnUnhandledException;
        // The tray menu is WinForms: give it modern visuals and follow dark mode.
        System.Windows.Forms.Application.EnableVisualStyles();
#pragma warning disable WFO5001 // dark mode for WinForms is marked experimental
        System.Windows.Forms.Application.SetColorMode(System.Windows.Forms.SystemColorMode.System);
#pragma warning restore WFO5001
        TaskScheduler.UnobservedTaskException += (_, args) => args.SetObserved();

        // One tray icon per copy of the app.
        string folderId = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(ConfigStore.AppDir.ToLowerInvariant())))[..16];
        _singleInstance = new Mutex(true, @"Local\KlipperTrayIcon-" + folderId, out bool first);
        var showSignal = new EventWaitHandle(false, EventResetMode.AutoReset, @"Local\KlipperTrayIcon-show-" + folderId);
        if (!first)
        {
            // Already running (e.g. the exe was double-clicked again): open its
            // webcam popup instead. We were just launched by the user, so we may
            // pass on the right to take the foreground.
            Native.AllowSetForegroundWindow(Native.ASFW_ANY);
            showSignal.Set();
            Shutdown();
            return;
        }
        var signalThread = new Thread(() =>
        {
            while (showSignal.WaitOne())
                Dispatcher.InvokeAsync(() => _tray?.ShowFromSecondLaunch());
        }) { IsBackground = true, Name = "Second launch listener" };
        signalThread.Start();

        AppConfig? config;
        try
        {
            config = ConfigStore.Load();
        }
        catch (Exception ex) when (ex is JsonException or IOException)
        {
            MessageBox.Show($"config.json couldn't be read, so the settings window will open with defaults.\n\n{ex.Message}",
                            "Klipper Tray Icon", MessageBoxButton.OK, MessageBoxImage.Warning);
            config = null;
        }

        if (config == null)
        {
            // First launch: set up before anything appears in the tray.
            var setup = new SettingsWindow(new AppConfig(), firstRun: true);
            if (setup.ShowDialog() != true || setup.Result == null)
            {
                Shutdown();
                return;
            }
            config = setup.Result;
            ConfigStore.Save(config);
        }

        Autostart.RefreshPath();
        _tray = new TrayController(config);
        if (Array.IndexOf(e.Args, "--settings") >= 0)
            _tray.OpenSettings();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _tray?.Dispose();
        _singleInstance?.Dispose();
        base.OnExit(e);
    }

    private void OnUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        // Keep the tray icon alive; note the problem next to the exe.
        try
        {
            File.AppendAllText(Path.Combine(ConfigStore.AppDir, "error.log"),
                               $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {e.Exception}\n\n");
        }
        catch (Exception) { }
        e.Handled = true;
    }
}
