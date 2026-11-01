using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
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
    // The last refresh's uploads and file details, so a change can be shown before the printer confirms it.
    private IReadOnlyList<GcodeFile> _recentFiles = [];
    private Dictionary<string, GcodeDetails> _details = new();
    // Counted up by each change: a refresh that started before one would undo it on screen for a moment.
    private int _queueEdits, _queueBusy;
    // Dragging a row: pressed (not yet moved far enough to be a drag), then dragged.
    private QueueRow? _pressRow;
    private FrameworkElement? _pressElement;
    private Point _pressPoint;
    private QueueRow? _dragRow;
    private FrameworkElement? _dragContainer;
    private bool _dragFromQueue;
    private int _dropIndex = -1; // where it lands in the queue; -1 when it's not over the queue

    private void SetUpQueue(IPrintQueue queue, bool consoleToo)
    {
        Resources["GrabCursor"] = HandCursors.Open;
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
            var (showing, edits) = await Dispatcher.InvokeAsync(() => (IsVisible && _queueShown, _queueEdits));
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
                // One more than is shown: the upload that moves up when one is added to the queue.
                var paths = status.Jobs.Select(j => j.Path)
                    .Concat(RecentToShow(recent, status, _recentCount + 1).Select(f => f.Path));
                foreach (string path in paths.Distinct())
                {
                    try { details[path] = await _queue.GetFileDetailsAsync(path, ct).ConfigureAwait(false); }
                    catch (Exception) when (!ct.IsCancellationRequested) { details[path] = new GcodeDetails(null, null); }
                }
                var files = recent;
                await Dispatcher.InvokeAsync(() => ShowQueue(status, files, details, edits));
            }
            catch (OperationCanceledException) { break; }
            catch (Exception e)
            {
                string message = _backend.DescribeError(e);
                await Dispatcher.InvokeAsync(() => QueueStateText.Text = message);
            }
        }
    }

    private void ShowQueue(QueueStatus status, IReadOnlyList<GcodeFile> recent, Dictionary<string, GcodeDetails> details, int edits)
    {
        if (edits != _queueEdits || _queueBusy > 0)
            return; // from before a change: the refresh after it shows it
        (_queueStatus, _recentFiles, _details) = (status, recent, details);
        if (_dragRow == null) // don't pull the lists out from under a drag
        {
            Replace(_queueRows, status.Jobs.Select(j => QueueRowFor(j.Path, j.JobId)));
            ShowRecent();
        }
        ShowQueueCount();
        UpdateStartButton();
    }

    private QueueRow QueueRowFor(string path, string jobId)
    {
        var d = _details.GetValueOrDefault(path);
        return new QueueRow(path, DisplayName(path), d?.EstimatedSeconds is double t ? StatusText.FormatDuration(t) : "",
                            Image(path, d?.Thumbnail), Image(path + "#preview", d?.Preview), jobId);
    }

    /// <summary>The uploads that aren't in the queue as it's shown.</summary>
    private void ShowRecent()
    {
        var queued = _queueRows.Select(r => r.Path).ToHashSet();
        var shown = RecentToShow(_recentFiles, queued, _recentCount);
        Replace(_recentRows, shown.Select(f =>
        {
            var d = _details.GetValueOrDefault(f.Path);
            string age = Ago(DateTimeOffset.FromUnixTimeMilliseconds((long)(f.Modified * 1000)));
            string detail = d?.EstimatedSeconds is double t ? $"{StatusText.FormatDuration(t)} · {age}" : age;
            return new QueueRow(f.Path, DisplayName(f.Path), detail, Image(f.Path, d?.Thumbnail), Image(f.Path + "#preview", d?.Preview));
        }));
        LoadMoreButton.Visibility = _recentFiles.Count(f => !queued.Contains(f.Path)) > shown.Count ? Visibility.Visible : Visibility.Collapsed;
    }

    private void ShowQueueCount()
    {
        QueueEmpty.Visibility = _queueRows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        QueueHeader.Text = _queueRows.Count switch { 0 => "QUEUE", 1 => "QUEUE · 1 job", var n => $"QUEUE · {n} jobs" };
    }

    /// <summary>The newest uploads that aren't in the queue (removing one from the queue brings it back).</summary>
    internal static List<GcodeFile> RecentToShow(IReadOnlyList<GcodeFile> recent, QueueStatus status, int count) =>
        RecentToShow(recent, status.Jobs.Select(j => j.Path).ToHashSet(), count);

    private static List<GcodeFile> RecentToShow(IReadOnlyList<GcodeFile> recent, ISet<string> queued, int count) =>
        recent.Where(f => !queued.Contains(f.Path)).Take(count).ToList();

    private void OnLoadMoreClick(object sender, RoutedEventArgs e)
    {
        _recentCount += RecentCount;
        _queueWake = true;
    }

    /// <summary>
    /// Makes a list match, touching only the rows that changed: the others keep their place on
    /// screen, so nothing flickers or jumps.
    /// </summary>
    internal static void Replace<T>(ObservableCollection<T> rows, IEnumerable<T> fresh)
    {
        var list = fresh.ToList();
        for (int i = 0; i < list.Count; i++)
        {
            if (i < rows.Count && Equals(rows[i], list[i]))
                continue;
            int later = -1;
            for (int j = i + 1; j < rows.Count && later < 0; j++)
                if (Equals(rows[j], list[i]))
                    later = j;
            if (later >= 0)
                rows.Move(later, i); // a row above it went, or it moved up
            else if (i < rows.Count && !list.Contains(rows[i]))
                rows[i] = list[i]; // the same place, changed (its time, or the job id once it's queued)
            else
                rows.Insert(i, list[i]);
        }
        while (rows.Count > list.Count)
            rows.RemoveAt(rows.Count - 1);
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
            // At once: it goes to the end of the queue and the next upload moves up into the list.
            _queueRows.Add(QueueRowFor(row.Path, ""));
            ShowRecent();
            ShowQueueCount();
            await QueueActionAsync(q => q.AddToQueueAsync(row.Path, _lifetime.Token), "Couldn't add it");
        }
    }

    private async void OnQueueRemoveClick(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is QueueRow row)
        {
            _queueRows.Remove(row); // at once (back among the uploads); the next refresh confirms it
            ShowRecent();
            ShowQueueCount();
            await QueueActionAsync(q => q.RemoveFromQueueAsync(row.JobId, _lifetime.Token), "Couldn't remove it");
        }
    }

    // ---- dragging: queued jobs to reorder them, recent uploads into the queue -------------


    private void OnRowMouseDown(object sender, MouseButtonEventArgs e)
    {
        // Buttons in the row (✕, ＋) take their own clicks before this sees them.
        if (sender is FrameworkElement { DataContext: QueueRow row } element)
            (_pressRow, _pressElement, _pressPoint) = (row, element, e.GetPosition(QueueContent));
    }

    private void OnRowMouseMove(object sender, MouseEventArgs e)
    {
        if (_pressRow == null || e.LeftButton != MouseButtonState.Pressed)
            return;
        var at = e.GetPosition(QueueContent);
        if (_dragRow == null)
        {
            if (Math.Abs(at.Y - _pressPoint.Y) < SystemParameters.MinimumVerticalDragDistance
                && Math.Abs(at.X - _pressPoint.X) < SystemParameters.MinimumHorizontalDragDistance)
                return; // still a click
            StartDrag();
        }
        // Near the top or bottom of the list, scroll it along.
        double y = e.GetPosition(QueueScroll).Y;
        if (y < 24)
            QueueScroll.ScrollToVerticalOffset(QueueScroll.VerticalOffset - 8);
        else if (y > QueueScroll.ActualHeight - 24)
            QueueScroll.ScrollToVerticalOffset(QueueScroll.VerticalOffset + 8);
        at = e.GetPosition(QueueContent);
        _dropIndex = DropIndex(at.Y);
        OpenGap();
        // The row follows the mouse (a recent upload's list may have moved down to make room).
        if (_dragContainer?.RenderTransform is TranslateTransform move)
            move.Y = at.Y - _pressPoint.Y - (_dragFromQueue ? 0 : BelowQueueShift.Y);
    }

    private TranslateTransform BelowQueueShift => (TranslateTransform)BelowQueue.RenderTransform;

    private void StartDrag()
    {
        _dragRow = _pressRow;
        _dragFromQueue = _queueRows.Contains(_dragRow!);
        var list = _dragFromQueue ? QueueList : RecentList;
        _dragContainer = list.ItemContainerGenerator.ContainerFromItem(_dragRow!) as FrameworkElement;
        if (_dragContainer != null)
        {
            _dragContainer.RenderTransform = new TranslateTransform();
            Panel.SetZIndex(_dragContainer, 1);
            _dragContainer.Opacity = 0.85;
        }
        _pressElement?.CaptureMouse(); // keep getting the mouse as the row moves away from under it
        Mouse.OverrideCursor = HandCursors.Closed; // holding it
    }

    /// <summary>
    /// The queued rows other than the one being dragged, with where they'd be without the gap
    /// (layout positions, not where they've slid to).
    /// </summary>
    private List<(QueueRow Row, FrameworkElement Container, double Top, double Bottom)> OtherQueueRows()
    {
        var rows = new List<(QueueRow, FrameworkElement, double, double)>();
        double listTop = QueueList.TranslatePoint(new Point(0, 0), QueueContent).Y;
        foreach (var row in _queueRows)
        {
            if (row == _dragRow || QueueList.ItemContainerGenerator.ContainerFromItem(row) is not FrameworkElement c)
                continue;
            double top = listTop + VisualTreeHelper.GetOffset(c).Y;
            rows.Add((row, c, top, top + c.ActualHeight));
        }
        return rows;
    }

    /// <summary>Where the dragged row would land: before the first queued row whose middle is below the mouse.</summary>
    private int DropIndex(double y)
    {
        // A recent upload only goes in when it's over the queue (above the recent uploads' heading).
        if (!_dragFromQueue && y > RecentHeading.TranslatePoint(new Point(0, 0), QueueContent).Y + RecentHeading.ActualHeight)
            return -1;
        var others = OtherQueueRows();
        int index = others.Count(r => (r.Top + r.Bottom) / 2 < y);
        // Dropping a queued job back where it was changes nothing.
        return _dragFromQueue && index == _queueRows.IndexOf(_dragRow!) ? -1 : index;
    }

    /// <summary>
    /// Opens a gap in the queue where the dragged row will land: the rows after it slide down
    /// (and a queued job's old place closes up); a recent upload pushes everything below the
    /// queue down too.
    /// </summary>
    private void OpenGap()
    {
        double height = _dragContainer?.ActualHeight ?? 0;
        int from = _dragFromQueue ? _queueRows.IndexOf(_dragRow!) : -1;
        int to = _dropIndex >= 0 ? _dropIndex : from; // no gap: everything back in place
        var others = OtherQueueRows();
        for (int i = 0; i < others.Count; i++)
        {
            // Without the dragged job, rows after its old place move up; at or after the gap, down.
            double shift = (from >= 0 && i >= from ? -height : 0) + (to >= 0 && i >= to ? height : 0);
            Slide(others[i].Container, shift);
        }
        if (!_dragFromQueue)
            BelowQueueShift.Y = _dropIndex >= 0 ? height : 0;
    }

    /// <summary>Slides a row to y (relative to its place), quickly; the target is kept in Tag.</summary>
    private static void Slide(FrameworkElement row, double y)
    {
        if (row.Tag is double target && target == y)
            return; // already there or on its way
        if (row.RenderTransform is not TranslateTransform move)
            row.RenderTransform = move = new TranslateTransform();
        row.Tag = y;
        move.BeginAnimation(TranslateTransform.YProperty,
                           new System.Windows.Media.Animation.DoubleAnimation(y, TimeSpan.FromMilliseconds(90)));
    }

    private void OnRowMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (_dragRow != null)
            FinishDrag(drop: true);
        _pressRow = null;
    }

    // E.g. the window lost focus mid-drag: put the row back.
    private void OnRowLostCapture(object sender, MouseEventArgs e)
    {
        if (_dragRow != null)
            FinishDrag(drop: false);
    }

    private async void FinishDrag(bool drop)
    {
        var row = _dragRow!;
        bool fromQueue = _dragFromQueue;
        int to = _dropIndex;
        if (_dragContainer != null)
        {
            _dragContainer.RenderTransform = Transform.Identity;
            _dragContainer.Opacity = 1;
            Panel.SetZIndex(_dragContainer, 0);
        }
        // Close the gap; the lists then show the new order.
        foreach (var other in OtherQueueRows())
            (other.Container.RenderTransform, other.Container.Tag) = (Transform.Identity, null);
        BelowQueueShift.Y = 0;
        (_dragRow, _dragContainer, _pressRow, _dropIndex) = (null, null, null, -1);
        Mouse.OverrideCursor = null;
        _pressElement?.ReleaseMouseCapture();
        if (!drop || to < 0)
            return;
        if (fromQueue)
        {
            _queueRows.Move(_queueRows.IndexOf(row), to);
            var order = _queueRows.Select(r => r.Path).ToList();
            await QueueActionAsync(q => q.ReorderQueueAsync(order, _lifetime.Token), "Couldn't reorder");
        }
        else
        {
            // Shown in place at once; the printer adds it at the end, then it's moved there.
            _queueRows.Insert(to, QueueRowFor(row.Path, ""));
            ShowRecent();
            ShowQueueCount();
            bool last = to == _queueRows.Count - 1;
            var order = _queueRows.Select(r => r.Path).ToList();
            await QueueActionAsync(async q =>
            {
                await q.AddToQueueAsync(row.Path, _lifetime.Token);
                if (!last)
                    await q.ReorderQueueAsync(order, _lifetime.Token);
            }, "Couldn't add it");
        }
    }

    /// <summary>
    /// Runs a change on the printer, then refreshes (which also puts things back if it failed);
    /// a failure shows in the header.
    /// </summary>
    private async Task QueueActionAsync(Func<IPrintQueue, Task> action, string failure)
    {
        Exception? error = null;
        _queueEdits++;
        _queueBusy++;
        try { await Task.Run(() => action(_queue!)); }
        catch (Exception e) when (!_lifetime.IsCancellationRequested) { error = e; }
        finally
        {
            _queueBusy--;
            _queueEdits++;
            _queueWake = true; // show the result now
        }
        if (error != null)
        {
            // In the header for a few seconds, then the usual button or note again.
            _actionError = $"{failure}: {_backend.DescribeError(error)}";
            UpdateStartButton();
            await Task.Delay(TimeSpan.FromSeconds(5));
            _actionError = null;
            UpdateStartButton();
        }
    }
}
