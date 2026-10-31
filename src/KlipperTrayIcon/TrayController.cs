using System;
using System.Diagnostics;
using System.Drawing;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Threading;
using KlipperTrayIcon.Printers;
using KlipperTrayIcon.UI;
using Forms = System.Windows.Forms;

namespace KlipperTrayIcon;

/// <summary>The tray icon, its menu, and the loop that keeps them up to date.</summary>
public sealed class TrayController : IDisposable
{
    private readonly Dispatcher _ui = Dispatcher.CurrentDispatcher;
    private readonly Forms.NotifyIcon _icon;
    private readonly Forms.ToolStripMenuItem[] _statusItems = new Forms.ToolStripMenuItem[3];
    private readonly Forms.ToolStripMenuItem _webcamItem;
    private readonly Forms.ToolStripMenuItem _dashboardItem;
    private readonly CancellationTokenSource _shutdown = new();

    private AppConfig _config;
    private IPrinterBackend _backend;
    private WebcamPopup? _popup;
    private SettingsWindow? _settings;
    private PrinterStatus _status = PrinterStatus.Connecting;
    // Last status actually heard from the printer. Notifications compare
    // against this, so a brief network drop mid-print doesn't swallow the
    // "print finished" notification.
    private PrinterStatus _lastOnline = PrinterStatus.Connecting;
    private (string State, double Progress)? _lastDrawn;
    private DateTime? _configTime;
    // Released to check the printer right away (the popup's stats strip just opened).
    private readonly SemaphoreSlim _statusWake = new(0);
    private static readonly TimeSpan StatsInterval = TimeSpan.FromSeconds(1);

    public TrayController(AppConfig config)
    {
        _config = config;
        _configTime = ConfigStore.LastWriteTime();

        var menu = new Forms.ContextMenuStrip { ShowImageMargin = false };
        for (int i = 0; i < _statusItems.Length; i++)
        {
            _statusItems[i] = new Forms.ToolStripMenuItem { Enabled = false };
            menu.Items.Add(_statusItems[i]);
        }
        menu.Items.Add(new Forms.ToolStripSeparator());
        _webcamItem = new Forms.ToolStripMenuItem("Show webcam", null, (_, _) => ShowWebcam());
        _webcamItem.Font = new Font(_webcamItem.Font, System.Drawing.FontStyle.Bold); // the click action
        menu.Items.Add(_webcamItem);
        _dashboardItem = new Forms.ToolStripMenuItem("Open web interface", null, (_, _) => OpenDashboard());
        menu.Items.Add(_dashboardItem);
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add(new Forms.ToolStripMenuItem("Settings…", null, (_, _) => OpenSettings()));
        menu.Items.Add(new Forms.ToolStripMenuItem("Exit", null, (_, _) => Exit()));
        menu.Opening += (_, _) => RefreshMenu();

        _icon = new Forms.NotifyIcon
        {
            ContextMenuStrip = menu,
            Text = "Connecting...",
            Icon = TrayIconRenderer.DrawIcon("standby", 0, IconSize),
            Visible = true,
        };
        _icon.MouseClick += (_, e) =>
        {
            if (e.Button == Forms.MouseButtons.Left)
                ToggleWebcam();
        };

        _backend = PrinterBackends.Create(config);
        _backend.Start();
        CreatePopup();
        _ = Task.Run(() => StatusLoopAsync(_shutdown.Token));
    }

    private static int IconSize => Forms.SystemInformation.SmallIconSize.Width;

    // ---- menu ------------------------------------------------------------

    private void RefreshMenu()
    {
        var lines = StatusText.Lines(_status);
        for (int i = 0; i < _statusItems.Length; i++)
        {
            _statusItems[i].Visible = i < lines.Count;
            _statusItems[i].Text = i < lines.Count ? StatusText.Shorten(lines[i], 48).Replace("&", "&&") : "";
        }
        _webcamItem.Visible = _backend.SupportsWebcam;
        _dashboardItem.Visible = _backend.DashboardUrl != null;
    }

    private void ShowWebcam() => _popup?.ShowAtCursor();

    /// <summary>The exe was launched again while running.</summary>
    public void ShowFromSecondLaunch()
    {
        if (_popup != null)
            _popup.ShowAtCursor();
        else
            OpenSettings();
    }

    private void ToggleWebcam()
    {
        if (_popup != null)
            _popup.Toggle();
        else
            OpenSettings(); // nothing to show for this printer; settings is the next most useful thing
    }

    private void OpenDashboard()
    {
        if (_backend.DashboardUrl is string url)
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
    }

    public void OpenSettings()
    {
        if (_settings != null)
        {
            _settings.Activate();
            return;
        }
        _settings = new SettingsWindow(_config.Clone(), firstRun: false);
        _settings.Closed += (_, _) =>
        {
            var result = _settings.Result;
            _settings = null;
            if (result == null)
                return;
            try
            {
                ConfigStore.Save(result);
            }
            catch (Exception e)
            {
                System.Windows.MessageBox.Show($"Couldn't save settings to\n{ConfigStore.ConfigPath}\n\n{e.Message}",
                                               "Klipper Tray Icon", System.Windows.MessageBoxButton.OK,
                                               System.Windows.MessageBoxImage.Error);
                return;
            }
            _configTime = ConfigStore.LastWriteTime();
            ApplyConfig(result);
        };
        _settings.Show();
        _settings.Activate();
    }

    private void Exit()
    {
        Dispose();
        System.Windows.Application.Current.Shutdown();
    }

    // ---- config ------------------------------------------------------------

    private void CreatePopup()
    {
        _popup = _backend.SupportsWebcam
            ? new WebcamPopup(_backend, _config.WebcamConsole && _backend.SupportsConsole, _config.ConsoleHideTimelapse)
            : null;
        if (_popup is { HasStats: true } popup)
            popup.IsVisibleChanged += (_, _) =>
            {
                if (popup.IsVisible && _statusWake.CurrentCount == 0)
                    _statusWake.Release();
            };
    }

    /// <summary>Switch to new settings without restarting (UI thread).</summary>
    private void ApplyConfig(AppConfig config)
    {
        var oldBackend = _backend;
        var oldPopup = _popup;
        _config = config;
        _backend = PrinterBackends.Create(config);
        _backend.Start();
        oldPopup?.Shutdown();
        oldBackend.Dispose();
        CreatePopup();
        _status = _lastOnline = PrinterStatus.Connecting;
        _lastDrawn = null;
    }

    /// <summary>Picks up hand edits to config.json (worker thread).</summary>
    private void ReloadConfigIfChanged()
    {
        var time = ConfigStore.LastWriteTime();
        if (time == _configTime)
            return;
        _configTime = time;
        AppConfig? config;
        try { config = ConfigStore.Load(); }
        catch (Exception) { return; } // half-written or badly edited: keep the current settings
        if (config != null)
            _ui.Invoke(() => ApplyConfig(config));
    }

    // ---- status loop ----------------------------------------------------------

    private async Task StatusLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            ReloadConfigIfChanged();
            var backend = _backend;
            PrinterStatus status;
            try { status = await backend.GetStatusAsync(ct); }
            catch (OperationCanceledException) { break; }
            catch (Exception e) { status = PrinterStatus.Offline(backend.DescribeError(e)); }

            bool statsShown = false;
            await _ui.InvokeAsync(() =>
            {
                statsShown = _popup is { HasStats: true, IsVisible: true };
                if (backend != _backend)
                    return; // settings changed while we were asking the old printer
                Apply(status);
            });

            // Faster while the stats strip is on screen.
            var delay = statsShown && StatsInterval < _config.UpdateInterval ? StatsInterval : _config.UpdateInterval;
            try { await _statusWake.WaitAsync(delay, ct); }
            catch (OperationCanceledException) { break; }
        }
    }

    /// <summary>UI thread.</summary>
    private void Apply(PrinterStatus status)
    {
        _status = status;
        _popup?.ShowStatus(status);
        if (status.State != "offline")
        {
            Notify(_lastOnline, status);
            _lastOnline = status;
        }

        // Windows caps tray tooltips at 127 characters.
        _icon.Text = StatusText.Shorten(string.Join("\n", StatusText.Lines(status)), 127);

        var drawn = (status.State, Math.Round(status.Progress, 2));
        if (drawn != _lastDrawn)
        {
            _lastDrawn = drawn;
            var old = _icon.Icon;
            _icon.Icon = TrayIconRenderer.DrawIcon(status.State, status.Progress, IconSize);
            old?.Dispose();
        }
    }

    private void Notify(PrinterStatus old, PrinterStatus now)
    {
        if (!_config.Notifications || old.State != "printing")
            return;
        string name = now.Filename.Length > 0 ? now.Filename : old.Filename.Length > 0 ? old.Filename : "Your print";
        (string Title, string Body)? message = now.State switch
        {
            "complete" => ("Print finished", $"{name} is done."),
            "error" => ("Print failed", $"{name} stopped with a printer error."),
            "cancelled" => ("Print cancelled", $"{name} was cancelled."),
            "paused" => ("Print paused", $"{name} is paused."),
            _ => null,
        };
        if (message is var (title, body))
            _icon.ShowBalloonTip(5000, title, body, Forms.ToolTipIcon.Info);
    }

    public void Dispose()
    {
        if (_shutdown.IsCancellationRequested)
            return;
        _shutdown.Cancel();
        _popup?.Shutdown();
        _settings?.Close();
        _backend.Dispose();
        _icon.Visible = false;
        _icon.Icon?.Dispose();
        _icon.Dispose();
    }
}
