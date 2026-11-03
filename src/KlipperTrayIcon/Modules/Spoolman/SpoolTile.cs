using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using KlipperTrayIcon.Printers;

namespace KlipperTrayIcon.Modules.Spoolman;

/// <summary>The stats strip's SPOOL tile: what's left on the spool in use, and its material and percentage (amber when it's short).</summary>
public sealed class SpoolTile(SpoolmanSource source, CancellationToken lifetime, Levels levels) : ModuleTile
{
    // Asked again at most this often (Show is called about every second while the popup is open).
    private static readonly TimeSpan AskEvery = TimeSpan.FromSeconds(5);

    private SpoolmanState? _state;
    private readonly Stopwatch _sinceAsked = new();
    private bool _asking;

    public override StatusText.Tile Show(PrinterStatus status)
    {
        if (!_asking && (!_sinceAsked.IsRunning || _sinceAsked.Elapsed >= AskEvery))
            _ = AskAsync(status.Filename);
        return Spools.Tile(_state, status, levels);
    }

    private async Task AskAsync(string filename)
    {
        _asking = true;
        _sinceAsked.Restart();
        try
        {
            // Shared with the panel: if it just asked, this is its answer. Back on the UI thread (Show's).
            var state = await source.GetAsync(filename, AskEvery - TimeSpan.FromSeconds(1), lifetime);
            bool changed = !Equals(state, _state);
            _state = state;
            if (changed)
                Refresh();
        }
        catch (Exception) when (!lifetime.IsCancellationRequested)
        {
            // Unreachable, or no Spoolman: show it as unknown.
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
