using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using KlipperTrayIcon.Printers;

namespace KlipperTrayIcon.UI;

/// <summary>One file in the queue panel: a queued job, or a recent upload.</summary>
public sealed record QueueRow(string Path, string Name, string Detail, ImageSource? Thumbnail, ImageSource? Preview,
                              string JobId = "");

/// <summary>
/// The print queue panel, which takes the console's place when its title-bar
/// button is pressed: queued jobs (drag to reorder, ✕ to remove), recent
/// uploads that aren't queued (＋ to add, "Load more" for older ones) and a
/// start button that needs a second click to confirm.
/// </summary>
public partial class WebcamPopup
{
    private const int RecentCount = 5; // recent uploads shown at first, and added by each "Load more"
    private static readonly TimeSpan QueueRefresh = TimeSpan.FromSeconds(2), FilesRefresh = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan StartConfirmTime = TimeSpan.FromSeconds(4);

    private IPrintQueue? _queue;
    private bool _queueShown; // the panel shows the queue rather than the console
    private volatile bool _queueWake = true;
    private volatile int _recentCount = RecentCount;
    private readonly ObservableCollection<QueueRow> _queueRows = new();
    private readonly ObservableCollection<QueueRow> _recentRows = new();
    private QueueStatus? _queueStatus;
    private string _printerState = "standby";
    private bool _startArmed;
    private readonly DispatcherTimer _disarm = new() { Interval = StartConfirmTime };
    private readonly Dictionary<string, (byte[] Bytes, BitmapSource Image)> _thumbnails = new();
    // Dragging a queued job by its handle.
    private QueueRow? _dragRow;
    private FrameworkElement? _dragContainer;
    private int _dragFrom;

    private void SetUpQueue(IPrintQueue queue, bool consoleToo)
    {
        _queue = queue;
        QueueList.ItemsSource = _queueRows;
        RecentList.ItemsSource = _recentRows;
        _disarm.Tick += (_, _) => { _startArmed = false; _disarm.Stop(); UpdateStartButton(); };
        // Without the console, the panel is just the queue.
        QueueButton.Visibility = consoleToo ? Visibility.Visible : Visibility.Collapsed;
        ShowQueuePanel(!consoleToo);
        UpdateStartButton();
        _ = Task.Run(() => QueueLoopAsync(_lifetime.Token));
    }

    private void OnQueueButtonClick(object sender, RoutedEventArgs e) => ShowQueuePanel(!_queueShown);

    private void ShowQueuePanel(bool queue)
    {
        _queueShown = queue;
        QueueView.Visibility = queue ? Visibility.Visible : Visibility.Collapsed;
        ConsoleView.Visibility = queue ? Visibility.Collapsed : Visibility.Visible;
        QueueButton.Content = queue ? "" : ""; // console / list
        QueueButton.ToolTip = queue ? "Show the console" : "Show the print queue";
        if (queue)
            _queueWake = true;
    }

    // ---- refreshing ------------------------------------------------------------------

    private async Task QueueLoopAsync(CancellationToken ct)
    {
        var sinceQueue = System.Diagnostics.Stopwatch.StartNew();
        var sinceFiles = System.Diagnostics.Stopwatch.StartNew();
        IReadOnlyList<GcodeFile> recent = [];
        bool filesKnown = false;
        while (!ct.IsCancellationRequested)
        {
            bool showing = await Dispatcher.InvokeAsync(() => IsVisible && _queueShown);
            if (!_queueWake && !(showing && sinceQueue.Elapsed >= QueueRefresh))
            {
                try { await Task.Delay(200, ct).ConfigureAwait(false); } catch (OperationCanceledException) { break; }
                continue;
            }
            bool woken = _queueWake;
            _queueWake = false;
            sinceQueue.Restart();
            try
            {
                var status = await _queue!.GetQueueAsync(ct).ConfigureAwait(false);
                if (woken || !filesKnown || sinceFiles.Elapsed >= FilesRefresh)
                {
                    // Queued files are left out of the list, so ask for enough to fill it anyway, and one
                    // more to know whether "Load more" has anything to load.
                    recent = await _queue.GetRecentFilesAsync(_recentCount + status.Jobs.Count + 1, ct).ConfigureAwait(false);
                    filesKnown = true;
                    sinceFiles.Restart();
                }
                var details = new Dictionary<string, GcodeDetails>();
                var paths = status.Jobs.Select(j => j.Path)
                    .Concat(RecentToShow(recent, status, _recentCount).Select(f => f.Path));
                foreach (string path in paths.Distinct())
                {
                    try { details[path] = await _queue.GetFileDetailsAsync(path, ct).ConfigureAwait(false); }
                    catch (Exception) when (!ct.IsCancellationRequested) { details[path] = new GcodeDetails(null, null); }
                }
                var files = recent;
                await Dispatcher.InvokeAsync(() => ShowQueue(status, files, details));
            }
            catch (OperationCanceledException) { break; }
            catch (Exception e)
            {
                string message = _backend.DescribeError(e);
                await Dispatcher.InvokeAsync(() => QueueStateText.Text = message);
            }
        }
    }

    private void ShowQueue(QueueStatus status, IReadOnlyList<GcodeFile> recent, Dictionary<string, GcodeDetails> details)
    {
        _queueStatus = status;
        if (_dragRow == null) // don't pull the list out from under a drag
        {
            Replace(_queueRows, status.Jobs.Select(j =>
            {
                var d = details.GetValueOrDefault(j.Path);
                return new QueueRow(j.Path, DisplayName(j.Path), d?.EstimatedSeconds is double t ? StatusText.FormatDuration(t) : "",
                                    Image(j.Path, d?.Thumbnail), Image(j.Path + "#preview", d?.Preview), j.JobId);
            }));
        }
        var shown = RecentToShow(recent, status, _recentCount);
        Replace(_recentRows, shown.Select(f =>
        {
            var d = details.GetValueOrDefault(f.Path);
            string age = Ago(DateTimeOffset.FromUnixTimeMilliseconds((long)(f.Modified * 1000)));
            string detail = d?.EstimatedSeconds is double t ? $"{StatusText.FormatDuration(t)} · {age}" : age;
            return new QueueRow(f.Path, DisplayName(f.Path), detail, Image(f.Path, d?.Thumbnail), Image(f.Path + "#preview", d?.Preview));
        }));
        LoadMoreButton.Visibility = recent.Count(f => !IsQueued(f, status)) > shown.Count ? Visibility.Visible : Visibility.Collapsed;
        QueueEmpty.Visibility = status.Jobs.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        QueueHeader.Text = status.Jobs.Count switch { 0 => "QUEUE", 1 => "QUEUE · 1 job", var n => $"QUEUE · {n} jobs" };
        UpdateStartButton();
    }

    /// <summary>The newest uploads that aren't in the queue (removing one from the queue brings it back).</summary>
    internal static List<GcodeFile> RecentToShow(IReadOnlyList<GcodeFile> recent, QueueStatus status, int count) =>
        recent.Where(f => !IsQueued(f, status)).Take(count).ToList();

    private static bool IsQueued(GcodeFile file, QueueStatus status) => status.Jobs.Any(j => j.Path == file.Path);

    private void OnLoadMoreClick(object sender, RoutedEventArgs e)
    {
        _recentCount += RecentCount;
        _queueWake = true;
    }

    /// <summary>Updates a list only if it changed, so it doesn't flicker every refresh.</summary>
    private static void Replace(ObservableCollection<QueueRow> rows, IEnumerable<QueueRow> fresh)
    {
        var list = fresh.ToList();
        if (rows.SequenceEqual(list))
            return;
        rows.Clear();
        foreach (var row in list)
            rows.Add(row);
    }

    /// <summary>Decodes a thumbnail once and reuses it, so unchanged rows stay equal and don't rebuild.</summary>
    private ImageSource? Image(string key, byte[]? bytes)
    {
        if (bytes == null)
            return null;
        if (_thumbnails.TryGetValue(key, out var cached) && cached.Bytes == bytes)
            return cached.Image;
        if (TryDecode(bytes) is not { } image)
            return null;
        _thumbnails[key] = (bytes, image);
        return image;
    }

    /// <summary>"Benchy" for "parts/Benchy.gcode".</summary>
    internal static string DisplayName(string path)
    {
        string name = path[(path.LastIndexOf('/') + 1)..];
        return name.EndsWith(".gcode", StringComparison.OrdinalIgnoreCase) ? name[..^".gcode".Length] : name;
    }

    internal static string Ago(DateTimeOffset time, DateTimeOffset? now = null)
    {
        var age = (now ?? DateTimeOffset.Now) - time;
        return age.TotalMinutes < 1 ? "just now"
            : age.TotalHours < 1 ? $"{(int)age.TotalMinutes} min ago"
            : age.TotalDays < 1 ? $"{(int)age.TotalHours} h ago"
            : age.TotalDays < 2 ? "yesterday"
            : $"{(int)age.TotalDays} days ago";
    }

    // ---- start next -------------------------------------------------------------------

    /// <summary>The printer's state from the tray's status updates (for the start button).</summary>
    private void QueueSawStatus(PrinterStatus status)
    {
        _printerState = status.State;
        UpdateStartButton();
    }

    /// <summary>
    /// The header's right side: the start button when the next job can start,
    /// otherwise a short note on why not (or what will happen).
    /// </summary>
    private void UpdateStartButton()
    {
        if (_queue == null)
            return;
        var jobs = _queueStatus?.Jobs ?? [];
        bool busy = _printerState is "printing" or "paused";
        string? note = _queueStatus == null ? "Loading…"
            : jobs.Count == 0 ? ""
            : _printerState == "offline" ? "Printer offline"
            : _queueStatus.State is "loading" or "starting" ? "Starting…"
            // Printing: the stats strip says so; only mention the queue if it will carry on by itself.
            : busy ? (_queueStatus.State == "ready" ? "Auto-start next" : "")
            : null; // can start
        if (note != null && _startArmed)
        {
            _startArmed = false;
            _disarm.Stop();
        }
        if (_actionError != null)
            note = _actionError;
        QueueStateText.Text = note ?? "";
        QueueStateText.Visibility = note != null ? Visibility.Visible : Visibility.Collapsed;
        StartNextButton.Visibility = note == null ? Visibility.Visible : Visibility.Collapsed;
        if (note != null)
            return;
        var text = new TextBlock();
        if (_startArmed)
            text.Inlines.Add(new System.Windows.Documents.Run("Confirm start"));
        else
        {
            text.Inlines.Add(new System.Windows.Documents.Run("▶ ") { Foreground = StartGreen });
            text.Inlines.Add(new System.Windows.Documents.Run("Start next"));
        }
        StartNextButton.Content = text;
        StartNextButton.ToolTip = $"Starts {DisplayName(jobs[0].Path)}";
        // Armed: amber, so the second click is clearly a different, deliberate one.
        StartNextButton.Background = _startArmed ? ConsoleLine.TemperatureBrush : StartIdle;
        StartNextButton.Foreground = _startArmed ? Brushes.Black : StartText;
        StartNextButton.BorderBrush = _startArmed ? ConsoleLine.TemperatureBrush : StartBorder;
    }

    private static readonly Brush StartGreen = Frozen(Color.FromRgb(0x5F, 0xC2, 0x7A)),
                                  StartIdle = Frozen(Color.FromRgb(0x26, 0x26, 0x26)),
                                  StartBorder = Frozen(Color.FromRgb(0x3A, 0x3A, 0x3A)),
                                  StartText = Frozen(Color.FromRgb(0xDA, 0xDA, 0xDA));
    // The last change that failed, shown in the header for a few seconds.
    private string? _actionError;

    private async void OnStartNextClick(object sender, RoutedEventArgs e)
    {
        if (!_startArmed)
        {
            // First click: ask for the second, which actually starts the printer.
            _startArmed = true;
            _disarm.Stop();
            _disarm.Start();
            UpdateStartButton();
            return;
        }
        _startArmed = false;
        _disarm.Stop();
        StartNextButton.Visibility = Visibility.Collapsed;
        QueueStateText.Text = "Starting…";
        QueueStateText.Visibility = Visibility.Visible;
        await QueueActionAsync(q => q.StartQueueAsync(_lifetime.Token), "Couldn't start");
    }

    // ---- add, remove, reorder ---------------------------------------------------------

    private async void OnQueueAddClick(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is QueueRow row)
        {
            _recentRows.Remove(row); // at once; the next refresh shows it in the queue
            await QueueActionAsync(q => q.AddToQueueAsync(row.Path, _lifetime.Token), "Couldn't add it");
        }
    }

    private async void OnQueueRemoveClick(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is QueueRow row)
        {
            _queueRows.Remove(row); // at once; the next refresh confirms it
            await QueueActionAsync(q => q.RemoveFromQueueAsync(row.JobId, _lifetime.Token), "Couldn't remove it");
        }
    }

    private void OnQueueDragStarted(object sender, DragStartedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not QueueRow row)
            return;
        _dragRow = row;
        _dragFrom = _queueRows.IndexOf(row);
        _dragContainer = QueueList.ItemContainerGenerator.ContainerFromItem(row) as FrameworkElement;
        if (_dragContainer != null)
        {
            _dragContainer.RenderTransform = new TranslateTransform();
            Panel.SetZIndex(_dragContainer, 1);
            _dragContainer.Opacity = 0.85;
        }
    }

    private void OnQueueDragDelta(object sender, DragDeltaEventArgs e)
    {
        // The row moves with the mouse; the others stay put until it's dropped.
        if (_dragContainer?.RenderTransform is TranslateTransform move)
            move.Y += e.VerticalChange;
    }

    private async void OnQueueDragCompleted(object sender, DragCompletedEventArgs e)
    {
        if (_dragRow == null || _dragContainer == null)
        {
            _dragRow = null;
            return;
        }
        double moved = (_dragContainer.RenderTransform as TranslateTransform)?.Y ?? 0;
        double rowHeight = Math.Max(1, _dragContainer.ActualHeight);
        int to = Math.Clamp(_dragFrom + (int)Math.Round(moved / rowHeight), 0, _queueRows.Count - 1);
        _dragContainer.RenderTransform = Transform.Identity;
        _dragContainer.Opacity = 1;
        Panel.SetZIndex(_dragContainer, 0);
        int from = _dragFrom;
        (_dragRow, _dragContainer) = (null, null);
        if (to == from)
            return;
        _queueRows.Move(from, to);
        var order = _queueRows.Select(r => r.Path).ToList();
        await QueueActionAsync(q => q.ReorderQueueAsync(order, _lifetime.Token), "Couldn't reorder");
    }

    /// <summary>Runs a change on the printer, then refreshes; a failure shows in the header.</summary>
    private async Task QueueActionAsync(Func<IPrintQueue, Task> action, string failure)
    {
        try
        {
            await Task.Run(() => action(_queue!));
            _queueWake = true; // show the result now
        }
        catch (Exception e) when (!_lifetime.IsCancellationRequested)
        {
            // In the header for a few seconds, then the usual button or note again.
            _actionError = $"{failure}: {_backend.DescribeError(e)}";
            UpdateStartButton();
            await Task.Delay(TimeSpan.FromSeconds(5));
            _actionError = null;
            UpdateStartButton();
        }
    }
}
