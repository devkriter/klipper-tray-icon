using System;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using KlipperTrayIcon.Printers;

namespace KlipperTrayIcon.Modules.Afc;

/// <summary>The stats strip's LANE tile: the tool in use and its filament; during a print's tool change, which one; amber on an AFC error.</summary>
public sealed class LaneTile(AfcSource source, CancellationToken lifetime) : ModuleTile
{
    // Asked again at most this often (Show is called about every second while the popup is open).
    private static readonly TimeSpan AskEvery = TimeSpan.FromSeconds(5);

    private AfcState? _state;
    private readonly Stopwatch _sinceAsked = new();
    private bool _asking;

    public override StatusText.Tile Show(PrinterStatus status)
    {
        if (!_asking && (!_sinceAsked.IsRunning || _sinceAsked.Elapsed >= AskEvery))
            _ = AskAsync();
        // The page fetched since (once a second while it shows): its state.
        if (_state != null && source.State is { } latest)
            _state = latest;
        return Describe(_state, status.State == "printing");
    }

    internal static StatusText.Tile Describe(AfcState? state, bool printing)
    {
        if (state == null)
            return new("—", "");
        string tool = state.Loaded?.Title ?? "—";
        if (state.Error)
            return new(tool, "Error", Highlight: true);
        if (state.Bypass)
            return new("—", "Bypass");
        if (printing && state.Busy && state.Changes > 0)
            return new(state.Working?.Title ?? tool, $"Change {state.Change} of {state.Changes}");
        if (state.Busy)
            return new(tool, state.Describe(printing));
        if (state.Loaded is not { } lane)
            return new("—", "Unloaded");
        return new(tool, string.Join(" ", new[] { lane.Filament, lane.Material }.Where(s => s.Length > 0)));
    }

    private async Task AskAsync()
    {
        _asking = true;
        _sinceAsked.Restart();
        try
        {
            // Shared with the page: if it just asked, this is its answer. Back on the UI thread (Show's).
            var state = await source.GetAsync(AskEvery - TimeSpan.FromSeconds(1), lifetime);
            bool changed = !state.Same(_state);
            _state = state;
            if (changed)
                Refresh();
        }
        catch (Exception) when (!lifetime.IsCancellationRequested)
        {
            if (_state != null)
            {
                _state = null;
                Refresh();
            }
        }
        catch (OperationCanceledException) { }
        finally { _asking = false; }
    }
}
