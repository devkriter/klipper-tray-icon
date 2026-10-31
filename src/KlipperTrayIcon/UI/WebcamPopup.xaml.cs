using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using KlipperTrayIcon.Printers;

namespace KlipperTrayIcon.UI;

/// <summary>
/// The webcam popup. Created once (hidden) when the tray starts, so a click
/// only has to show a window that already exists. While hidden it keeps a
/// recent snapshot ready, which appears the instant the popup opens and is
/// replaced as soon as live video arrives.
/// </summary>
public partial class WebcamPopup : Window
{
    private const double VideoWidth = 640, ConsoleWidth = 400, PopupHeight = 482;
    private static readonly TimeSpan SnapshotRefresh = TimeSpan.FromSeconds(20);
    // No frame for this long while open: the stream has stalled, so reconnect.
    private static readonly TimeSpan StallTimeout = TimeSpan.FromSeconds(10);
    // A tray click while the popup is open first takes focus away (hiding
    // it), then arrives as a click. Treat that pair as "toggle closed".
    private static readonly TimeSpan ToggleWindow = TimeSpan.FromMilliseconds(400);

    private readonly IPrinterBackend _backend;
    private readonly bool _showConsole;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly ConsoleFeed? _console;

    private CancellationTokenSource? _session;
    private int _sessionId;
    private bool _gotLive;
    private BitmapSource? _cached;
    private DateTime _cachedAt;
    private DateTime _hiddenAt;
    private DateTime _deactivatedAt;
    // Pinned: stays open, can sit behind other windows and remembers where
    // you put it, like a normal app. For this run of the app only.
    private bool _pinned;
    private bool _isShutdown;
    // Newest frame waiting for the UI thread (latest wins, so a slow UI never
    // builds up a backlog of frames): a decoded JPEG, or raw pixels to copy
    // into _video.
    private (int Session, string Kind, BitmapSource? Image, PixelFrame? Pixels)? _pending;
    private readonly object _pendingLock = new();
    // Reused for every raw frame of the same size (Bambu), instead of a new bitmap per frame.
    private WriteableBitmap? _video;

    public WebcamPopup(IPrinterBackend backend, bool showConsole, bool hideTimelapse = true)
    {
        InitializeComponent();
        _backend = backend;
        _showConsole = showConsole;

        ResetSize();
        MinWidth = VideoWidth / 2 + 2;
        MinHeight = PopupHeight / 2;
        if (showConsole)
        {
            ConsolePanel.Visibility = Visibility.Visible;
            Layout.SizeChanged += (_, _) => UpdateArrangement();
            UpdateArrangement();
            _console = new ConsoleFeed(backend, hideTimelapse, ConsoleList, ConsoleScroll, Dispatcher, _lifetime.Token);
            ShowStatus(PrinterStatus.Connecting);
        }

        Deactivated += (_, _) =>
        {
            _deactivatedAt = DateTime.UtcNow;
            HideFromFocusLoss();
        };
        StateChanged += (_, _) => FitMaximized();
        // handledEventsToo: the console's ScrollViewer marks clicks handled.
        AddHandler(MouseLeftButtonDownEvent, new MouseButtonEventHandler(OnDragAnywhere), handledEventsToo: true);
        KeyDown += (_, e) => { if (e.Key == Key.Escape) HidePopup(); };
        // Make the native window now, and render it once off-screen, so the
        // first real show doesn't pay for window creation or first-render setup.
        new WindowInteropHelper(this).EnsureHandle();
        WarmUp();

        _ = Task.Run(() => SnapshotLoopAsync(_lifetime.Token));
    }

    private void WarmUp()
    {
        bool showActivated = ShowActivated;
        ShowActivated = false;
        Left = Top = -32000; // off every screen
        Show();
        UpdateLayout();
        Hide();
        ShowActivated = showActivated;
    }

    // ---- show / hide ---------------------------------------------------------

    public void Toggle()
    {
        // Pinned and behind other windows: bring it forward rather than closing it.
        // (A tray click takes focus first, so "was it in front" means it lost focus just now.)
        if (IsVisible && _pinned && DateTime.UtcNow - _deactivatedAt > ToggleWindow)
            ShowAtCursor();
        else if (IsVisible)
            HidePopup();
        else if (DateTime.UtcNow - _hiddenAt > ToggleWindow)
            ShowAtCursor();
        // else: this click is what just closed it.
    }

    public void ShowAtCursor()
    {
        if (_isShutdown)
            return;
        if (IsVisible)
        {
            Activate();
            return;
        }
        if (!_pinned)
        {
            // Unpinned it's a popup again: default size, by the tray.
            WindowState = WindowState.Normal;
            ResetSize();
            PositionNearCursor();
        }

        // Paint the cached frame before the window appears, so the very first
        // thing on screen is a picture rather than "Connecting...".
        if (_cached != null)
        {
            SetImage(_cached);
            SetBadge($"Snapshot from {_cachedAt:HH:mm:ss} · connecting…");
        }
        else
        {
            VideoImage.Source = null;
            Placeholder.Text = "Connecting...";
            Placeholder.Visibility = Visibility.Visible;
            SetBadge(null);
        }

        Show();
        Activate();
        Native.SetForegroundWindow(new WindowInteropHelper(this).Handle);
        _console?.Resume();
        StartSession();
    }

    /// <summary>Centered above the cursor (the tray is usually bottom-right), kept on its monitor.</summary>
    private void PositionNearCursor()
    {
        var (cursor, work, scale) = Native.CursorMonitor();
        int width = (int)Math.Round(Width * scale), height = (int)Math.Round(Height * scale);
        int x = cursor.X - width / 2;
        int y = cursor.Y - height - (int)(20 * scale);
        x = Math.Max(work.Left, Math.Min(work.Right - width, x));
        y = Math.Max(work.Top, Math.Min(work.Bottom - height, y));
        var hwnd = new WindowInteropHelper(this).Handle;
        Native.SetWindowPos(hwnd, IntPtr.Zero, x, y, width, height, Native.SWP_NOZORDER | Native.SWP_NOACTIVATE);
    }

    private void HideFromFocusLoss()
    {
        if (IsVisible && !_pinned)
            HidePopup();
    }

    private void ResetSize()
    {
        Width = VideoWidth + 2 + (_showConsole ? ConsoleWidth : 0);
        Height = PopupHeight;
    }

    private void OnPinClick(object sender, RoutedEventArgs e)
    {
        _pinned = !_pinned;
        // Pinned it's a normal window: other windows can cover it, and it has a
        // taskbar button so you can get back to it.
        Topmost = !_pinned;
        ShowInTaskbar = _pinned;
        PinButton.Content = _pinned ? "" : ""; // filled pin / pin
        PinButton.Foreground = _pinned ? PinnedBrush : Brushes.White;
        PinButton.ToolTip = _pinned ? "Unpin: close when you click elsewhere" : "Pin: keep open when you click elsewhere";
        Activate();
    }

    private static readonly Brush PinnedBrush = Frozen(Color.FromRgb(0x60, 0xCD, 0xFF));

    /// <summary>Nothing in the popup takes clicks except its buttons, so it drags from anywhere.</summary>
    private void OnDragAnywhere(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState != MouseButtonState.Pressed || IsInButton(e.OriginalSource as DependencyObject))
            return;
        if (e.ClickCount == 2)
        {
            WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
            return;
        }
        try { DragMove(); } // the native move loop: dragging to a screen edge snaps
        catch (InvalidOperationException) { }
    }

    private static bool IsInButton(DependencyObject? element)
    {
        for (; element != null; element = element is Visual or System.Windows.Media.Media3D.Visual3D
                                     ? VisualTreeHelper.GetParent(element) : LogicalTreeHelper.GetParent(element))
            if (element is System.Windows.Controls.Primitives.ButtonBase)
                return true;
        return false;
    }

    /// <summary>
    /// A maximized window without a title bar overhangs the screen by its
    /// resize border; pad it back in so nothing is cut off.
    /// </summary>
    private void FitMaximized()
    {
        var border = SystemParameters.WindowResizeBorderThickness;
        Frame.Margin = WindowState == WindowState.Maximized
            ? new Thickness(border.Left + 4, border.Top + 4, border.Right + 4, border.Bottom + 4)
            : new Thickness(0);
    }

    private void HidePopup()
    {
        if (!IsVisible)
            return;
        _hiddenAt = DateTime.UtcNow;
        Hide();
        _console?.Pause();
        EndSession();
    }

    private void OnCloseClick(object sender, RoutedEventArgs e) => HidePopup();

    /// <summary>Closes the window for good (settings changed, or the app is exiting).</summary>
    public void Shutdown()
    {
        if (_isShutdown)
            return;
        _isShutdown = true;
        EndSession();
        _lifetime.Cancel();
        Close();
    }

    // ---- console placement ----------------------------------------------------------

    private const double MinStackedConsoleHeight = 220;

    /// <summary>
    /// Console beside the video or below it: whichever leaves the video
    /// bigger. Below, the video gets its natural height and the console the rest.
    /// </summary>
    internal static (bool Stacked, double VideoHeight) ChooseArrangement(double width, double height, double aspect)
    {
        double beside = Math.Min(Math.Max(0, width - ConsoleWidth) / aspect, height);
        double below = Math.Min(width / aspect, height - MinStackedConsoleHeight);
        return below > beside ? (true, Math.Max(0, below)) : (false, 0);
    }

    private void UpdateArrangement()
    {
        if (!_showConsole || Layout.ActualWidth <= 0)
            return;
        double aspect = VideoImage.Source is BitmapSource { PixelHeight: > 0 } src
            ? (double)src.PixelWidth / src.PixelHeight
            : 16.0 / 9;
        var (stacked, videoHeight) = ChooseArrangement(Layout.ActualWidth, Layout.ActualHeight, aspect);
        System.Windows.Controls.Grid.SetColumn(ConsolePanel, stacked ? 0 : 1);
        System.Windows.Controls.Grid.SetRow(ConsolePanel, stacked ? 1 : 0);
        ConsoleColumn.Width = new GridLength(stacked ? 0 : ConsoleWidth);
        VideoRow.Height = stacked ? new GridLength(videoHeight) : new GridLength(1, GridUnitType.Star);
        ConsoleRow.Height = stacked ? new GridLength(1, GridUnitType.Star) : new GridLength(0);
        ConsolePanel.BorderThickness = stacked ? new Thickness(0, 1, 0, 0) : new Thickness(1, 0, 0, 0);
        // Beside the video, the strip leaves room for the pin and close buttons above it.
        StatsList.Margin = new Thickness(6, 8, stacked ? 6 : 62, 9);
    }

    // ---- stats strip ---------------------------------------------------------------

    private static readonly string[] StatLabels = ["NOZZLE", "BED", "PROGRESS", "LAYER", "TIME LEFT"];

    /// <summary>True when the popup shows the stats strip (it comes with the console).</summary>
    public bool HasStats => _showConsole;

    /// <summary>Latest printer status, for the stats strip above the console (UI thread).</summary>
    public void ShowStatus(PrinterStatus status)
    {
        if (!_showConsole)
            return;
        var tiles = StatusText.StatsTiles(status);
        var items = new StatTile[tiles.Length];
        for (int i = 0; i < tiles.Length; i++)
            items[i] = new StatTile(StatLabels[i], tiles[i].Value, tiles[i].Detail,
                                    tiles[i].Highlight ? ConsoleLine.TemperatureBrush : ConsoleLine.ResponseBrush);
        StatsList.ItemsSource = items;
        bool showProgress = status.State is "printing" or "paused" or "complete";
        ProgressScale.ScaleX = showProgress ? Math.Clamp(status.Progress, 0, 1) : 0;
        ProgressBar.Fill = status.State == "paused" ? ConsoleLine.TemperatureBrush : ProgressFill;
    }

    private static readonly Brush ProgressFill = Frozen(Color.FromRgb(0x3F, 0xB9, 0x50));

    private static Brush Frozen(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }

    // ---- video -----------------------------------------------------------------

    private void StartSession()
    {
        EndSession();
        int id = ++_sessionId;
        _gotLive = false;
        var cts = _session = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        _ = Task.Run(() => StreamAsync(id, cts.Token));
        // A single snapshot usually arrives well before the stream's first frame.
        _ = Task.Run(() => QuickSnapshotAsync(id, cts.Token));
    }

    private void EndSession()
    {
        // The stream task notices, and its cleanup releases the camera
        // (Bambu: hangs up - the printer allows one viewer at a time).
        _session?.Cancel();
        _session?.Dispose();
        _session = null;
    }

    /// <summary>
    /// Keeps the live video going while the popup is open: a stream that ends
    /// or goes quiet (Wi-Fi drop, camera service restart) is reconnected.
    /// </summary>
    private async Task StreamAsync(int id, CancellationToken ct)
    {
        bool everLive = false;
        for (int attempt = 0; !ct.IsCancellationRequested; attempt++)
        {
            bool gotFrame = await StreamOnceAsync(id, ct).ConfigureAwait(false);
            if (ct.IsCancellationRequested)
                break;
            everLive |= gotFrame;
            if (gotFrame)
                attempt = 0;
            Post(id, everLive ? "reconnecting" : "unavailable", null);
            try { await Task.Delay(RetryDelay(attempt), ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { break; }
        }
    }

    internal static TimeSpan RetryDelay(int attempt) => TimeSpan.FromSeconds(Math.Min(10, 1 << Math.Min(attempt, 4)));

    /// <summary>One connection to the camera, until it ends or stalls. True if it showed any frames.</summary>
    private async Task<bool> StreamOnceAsync(int id, CancellationToken ct)
    {
        using var attempt = CancellationTokenSource.CreateLinkedTokenSource(ct);
        bool gotFrame = false;
        // Reset on every frame; fires (ending this connection) if they stop coming.
        attempt.CancelAfter(StallTimeout);
        try
        {
            await foreach (var frame in _backend.GetWebcamFramesAsync(attempt.Token).ConfigureAwait(false))
            {
                if (frame is PixelFrame pixels)
                {
                    gotFrame = true;
                    attempt.CancelAfter(StallTimeout);
                    Post(id, "live", null, pixels);
                    continue;
                }
                var image = frame is JpegFrame jpg ? TryDecode(jpg.Data) : null;
                frame.Dispose();
                if (image == null)
                    continue;
                gotFrame = true;
                attempt.CancelAfter(StallTimeout);
                Post(id, "live", image);
            }
        }
        catch (Exception) { }
        return gotFrame;
    }

    private async Task QuickSnapshotAsync(int id, CancellationToken ct)
    {
        var jpg = await _backend.GetWebcamSnapshotAsync(TimeSpan.FromSeconds(2), ct).ConfigureAwait(false);
        if (jpg != null && TryDecode(jpg) is { } image)
            Post(id, "snapshot", image);
    }

    /// <summary>
    /// While hidden, keep the cached picture reasonably fresh (Klipper only -
    /// a Bambu snapshot would hold the printer's only camera slot, so the
    /// cache just holds the last frame seen).
    /// </summary>
    private async Task SnapshotLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            bool visible = await Dispatcher.InvokeAsync(() => IsVisible);
            if (!visible)
            {
                var jpg = await _backend.GetWebcamSnapshotAsync(TimeSpan.FromSeconds(3), ct).ConfigureAwait(false);
                if (jpg != null && TryDecode(jpg) is { } image)
                    Post(0, "cache", image);
            }
            try { await Task.Delay(SnapshotRefresh, ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { break; }
        }
    }

    private static BitmapSource? TryDecode(byte[] jpg)
    {
        try
        {
            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.StreamSource = new MemoryStream(jpg);
            bitmap.EndInit();
            bitmap.Freeze(); // usable from the UI thread
            return bitmap;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private void Post(int session, string kind, BitmapSource? image, PixelFrame? pixels = null)
    {
        bool schedule;
        lock (_pendingLock)
        {
            // Never let a snapshot replace a live frame that's still waiting.
            if (kind == "snapshot" && _pending is { Kind: "live" })
                return;
            schedule = _pending == null;
            _pending?.Pixels?.Dispose(); // dropped unseen
            _pending = (session, kind, image, pixels);
        }
        if (schedule)
            Dispatcher.InvokeAsync(DrainPending, System.Windows.Threading.DispatcherPriority.Render);
    }

    private void DrainPending()
    {
        (int Session, string Kind, BitmapSource? Image, PixelFrame? Pixels)? item;
        lock (_pendingLock)
        {
            item = _pending;
            _pending = null;
        }
        if (item is not var (session, kind, image, pixels))
            return;

        if (kind == "cache")
        {
            (_cached, _cachedAt) = (image, DateTime.Now);
            return;
        }
        if (session != _sessionId || !IsVisible)
        {
            pixels?.Dispose();
            return; // from a stream that has since been closed
        }

        switch (kind)
        {
            case "live":
                _gotLive = true;
                if (pixels != null)
                    image = ShowPixels(pixels);
                (_cached, _cachedAt) = (image, DateTime.Now);
                SetImage(image!);
                SetBadge(null);
                break;
            case "snapshot" when !_gotLive:
                (_cached, _cachedAt) = (image, DateTime.Now);
                SetImage(image!);
                SetBadge("Snapshot · connecting to live video…");
                break;
            case "reconnecting":
                SetBadge("Live video stopped · reconnecting…");
                break;
            case "unavailable":
                if (_cached != null)
                    SetBadge("Live video unavailable · showing last snapshot");
                else
                {
                    Placeholder.Text = "Camera unavailable";
                    Placeholder.Visibility = Visibility.Visible;
                }
                break;
        }
    }

    private WriteableBitmap ShowPixels(PixelFrame frame)
    {
        using (frame)
        {
            if (_video == null || _video.PixelWidth != frame.Width || _video.PixelHeight != frame.Height)
                _video = new WriteableBitmap(frame.Width, frame.Height, 96, 96, PixelFormats.Bgr32, null);
            _video.WritePixels(new Int32Rect(0, 0, frame.Width, frame.Height), frame.Pixels, frame.Stride, 0);
        }
        return _video;
    }

    private void SetImage(BitmapSource image)
    {
        bool newShape = VideoImage.Source is not BitmapSource old
                        || old.PixelWidth * image.PixelHeight != image.PixelWidth * old.PixelHeight;
        VideoImage.Source = image;
        if (newShape)
            UpdateArrangement();
        Placeholder.Visibility = Visibility.Collapsed;
    }

    private void SetBadge(string? text)
    {
        BadgeText.Text = text ?? "";
        Badge.Visibility = text == null ? Visibility.Collapsed : Visibility.Visible;
    }
}

/// <summary>One tile of the stats strip.</summary>
public sealed record StatTile(string Label, string Value, string Detail, Brush Brush);

/// <summary>One row in the console panel.</summary>
public sealed class ConsoleLine
{
    // Klipper's responses (macro output, warnings, "// ..." lines) are what
    // you actually want to read, so they get the brightest colour. Commands
    // (often routine macros) are a softer blue so they don't compete.
    private static readonly Brush Response = Frozen("#ECECEC"), Command = Frozen("#82AEE0"),
                                  Error = Frozen("#FF7B72"), Temperature = Frozen("#E3B341"), Status = Frozen("#A0A0A0");
    private static readonly FontFamily Mono = new("Cascadia Mono, Consolas"), Ui = new("Segoe UI");

    internal static Brush ResponseBrush => Response;
    internal static Brush TemperatureBrush => Temperature;

    public string Time { get; private init; } = "";
    public string Text { get; private init; } = "";
    public Brush Brush { get; private init; } = Response;
    public FontStyle FontStyle { get; private init; } = FontStyles.Normal;
    public FontWeight FontWeight { get; private init; } = FontWeights.Normal;
    public FontFamily FontFamily { get; private init; } = Mono;
    public bool HasTime => Time.Length > 0;
    public bool IsTemperature { get; private init; }

    private static Brush Frozen(string hex)
    {
        var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
        brush.Freeze();
        return brush;
    }

    public static ConsoleLine FromEntry(ConsoleEntry entry)
    {
        string raw = entry.Message.Trim();
        string time = DateTimeOffset.FromUnixTimeMilliseconds((long)(entry.Time * 1000)).ToLocalTime().ToString("HH:mm:ss");
        if (entry.Type == "command")
            return new ConsoleLine { Time = time, Text = "> " + raw, Brush = Command };
        if (raw.StartsWith("!!"))
            return new ConsoleLine { Time = time, Text = StripPrefix(raw, "!!"), Brush = Error, FontWeight = FontWeights.SemiBold };
        if (StatusText.TemperatureReport().IsMatch(raw))
            return new ConsoleLine { Time = time, Text = raw, Brush = Temperature, IsTemperature = true };
        return new ConsoleLine { Time = time, Text = StripPrefix(raw, "//") };
    }

    // Macros from moonraker-timelapse (TIMELAPSE_TAKE_FRAME, _TIMELAPSE_NEW_FRAME,
    // SET_GCODE_VARIABLE MACRO=TIMELAPSE_..., HYPERLAPSE ...).
    private static readonly System.Text.RegularExpressions.Regex TimelapseMacro =
        new(@"\b_?(TIMELAPSE|HYPERLAPSE)\w*", System.Text.RegularExpressions.RegexOptions.IgnoreCase |
                                               System.Text.RegularExpressions.RegexOptions.Compiled);

    /// <summary>
    /// A command sent by the timelapse macros on every frame. Only commands
    /// are matched: Klipper's responses (including timelapse errors) always show.
    /// </summary>
    internal static bool IsTimelapseCommand(ConsoleEntry entry) =>
        entry.Type == "command" && TimelapseMacro.IsMatch(entry.Message);

    /// <summary>
    /// Klipper marks every line of a response with "// " (or "!! " for
    /// errors), including each line of multi-line messages. The colour already
    /// says what kind of line it is, so drop the markers.
    /// </summary>
    internal static string StripPrefix(string message, string prefix)
    {
        var lines = message.Replace("\r\n", "\n").Split('\n');
        for (int i = 0; i < lines.Length; i++)
        {
            string line = lines[i].TrimEnd();
            if (line.StartsWith(prefix))
                line = line[prefix.Length..].TrimStart();
            lines[i] = line;
        }
        return string.Join("\n", lines);
    }

    /// <summary>
    /// Adds a line to the console. Klipper prints a temperature report every
    /// second while it waits to heat (M109/M190); consecutive ones replace
    /// each other so there's one line that updates in place.
    /// </summary>
    internal static void AppendTo(IList<ConsoleLine> lines, ConsoleLine line)
    {
        if (line.IsTemperature && lines.Count > 0 && lines[^1].IsTemperature)
            lines[^1] = line;
        else
            lines.Add(line);
    }

    public static ConsoleLine StatusMessage(string text) =>
        new() { Text = text, Brush = Status, FontStyle = FontStyles.Italic, FontFamily = Ui };
}

/// <summary>
/// Polls Moonraker's console while the popup is visible (plus once up front,
/// so history is ready before the first click).
/// </summary>
internal sealed class ConsoleFeed
{
    private const int MaxLines = 500;
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(1);

    private readonly IPrinterBackend _backend;
    private readonly bool _hideTimelapse;
    private readonly ObservableCollection<ConsoleLine> _lines = new();
    private readonly System.Windows.Controls.ScrollViewer _scroll;
    private readonly System.Windows.Threading.Dispatcher _ui;
    private volatile bool _active;
    private volatile bool _wake = true;
    private bool _hasLines;

    public ConsoleFeed(IPrinterBackend backend, bool hideTimelapse, System.Windows.Controls.ItemsControl list,
                       System.Windows.Controls.ScrollViewer scroll, System.Windows.Threading.Dispatcher ui, CancellationToken ct)
    {
        _backend = backend;
        _hideTimelapse = hideTimelapse;
        _scroll = scroll;
        _ui = ui;
        list.ItemsSource = _lines;
        _lines.Add(ConsoleLine.StatusMessage("Loading console…"));
        _ = Task.Run(() => LoopAsync(ct));
    }

    public void Resume() { _active = true; _wake = true; }
    public void Pause() => _active = false;

    private async Task LoopAsync(CancellationToken ct)
    {
        double lastTime = 0;
        string? lastError = null;
        var sinceLastPoll = System.Diagnostics.Stopwatch.StartNew();
        while (!ct.IsCancellationRequested)
        {
            bool due = _wake || (_active && sinceLastPoll.Elapsed >= PollInterval);
            if (!due)
            {
                try { await Task.Delay(100, ct).ConfigureAwait(false); } catch (OperationCanceledException) { break; }
                continue;
            }
            _wake = false;
            sinceLastPoll.Restart();
            try
            {
                var entries = await _backend.GetConsoleMessagesAsync(ct).ConfigureAwait(false);
                var fresh = new List<ConsoleEntry>();
                foreach (var e in entries)
                    if (e.Time > lastTime)
                        fresh.Add(e);
                if (fresh.Count > 0)
                    lastTime = fresh[^1].Time;
                if (_hideTimelapse)
                    fresh.RemoveAll(ConsoleLine.IsTimelapseCommand);
                lastError = null;
                if (fresh.Count > 0 || !_hasLines)
                    await _ui.InvokeAsync(() => ShowEntries(fresh));
            }
            catch (OperationCanceledException) { break; }
            catch (Exception e)
            {
                string message = _backend.DescribeError(e);
                if (message != lastError)
                {
                    lastError = message;
                    await _ui.InvokeAsync(() => Append(ConsoleLine.StatusMessage($"Console unavailable: {message}")));
                }
            }
        }
    }

    private bool AtBottom => _scroll.VerticalOffset >= _scroll.ScrollableHeight - 2;

    private void ShowEntries(List<ConsoleEntry> entries)
    {
        if (!_hasLines)
        {
            // First successful fetch: replace the "Loading..." placeholder.
            _hasLines = true;
            _lines.Clear();
            if (entries.Count == 0)
            {
                _lines.Add(ConsoleLine.StatusMessage("No console messages yet."));
                return;
            }
        }
        bool follow = AtBottom;
        foreach (var entry in entries)
            ConsoleLine.AppendTo(_lines, ConsoleLine.FromEntry(entry));
        while (_lines.Count > MaxLines)
            _lines.RemoveAt(0);
        if (follow)
            _scroll.Dispatcher.InvokeAsync(_scroll.ScrollToEnd, System.Windows.Threading.DispatcherPriority.Loaded);
    }

    private void Append(ConsoleLine line)
    {
        bool follow = AtBottom;
        _lines.Add(line);
        if (follow)
            _scroll.Dispatcher.InvokeAsync(_scroll.ScrollToEnd, System.Windows.Threading.DispatcherPriority.Loaded);
    }
}
