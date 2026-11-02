using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using KlipperTrayIcon.Printers;

namespace KlipperTrayIcon.Modules.PrintQueue;

/// <summary>The stats strip's QUEUE tile: how many jobs are queued, and the next one's name.</summary>
public sealed class QueueTile(PrintQueueApi queue, CancellationToken lifetime) : ModuleTile
{
    // Asked again at most this often (Show is called about every second while the popup is open).
    private static readonly TimeSpan AskEvery = TimeSpan.FromSeconds(5);

    private QueueStatus? _status;
    private readonly Stopwatch _sinceAsked = new();
    private bool _asking;

    public override StatusText.Tile Show(PrinterStatus status)
    {
        if (!_asking && (!_sinceAsked.IsRunning || _sinceAsked.Elapsed >= AskEvery))
            _ = AskAsync();
        return Describe(_status);
    }

    /// <summary>The tile for this queue: "—" until it's known.</summary>
    internal static StatusText.Tile Describe(QueueStatus? queue) =>
        queue == null ? new("—", "")
        : queue.Jobs.Count == 0 ? new("0", "empty")
        : new(queue.Jobs.Count.ToString(), "next " + StatusText.DisplayName(queue.Jobs[0].Path));

    private async Task AskAsync()
    {
        _asking = true;
        _sinceAsked.Restart();
        try
        {
            // Back on the UI thread (Show's), like the rest of this class.
            var status = await queue.GetQueueAsync(lifetime);
            bool changed = Describe(status) != Describe(_status);
            _status = status;
            if (changed)
                Refresh();
        }
        catch (Exception) when (!lifetime.IsCancellationRequested)
        {
            // Unreachable, or no queue: show it as unknown.
            if (_status != null)
            {
                _status = null;
                Refresh();
            }
        }
        catch (OperationCanceledException) { }
        finally { _asking = false; }
    }
}
