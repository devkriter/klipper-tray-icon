using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using KlipperTrayIcon.Printers;

namespace KlipperTrayIcon.Modules.Spoolman;

/// <summary>
/// What's in the printer: Moonraker's link to Spoolman, the toolheads' spools (none on a printer without
/// T0…Tn spool macros), the spools themselves by id, and the slicer's filament data for the file printing.
/// Stale: Spoolman isn't answering, so the spools are the last ones known.
/// </summary>
public sealed record SpoolmanState(SpoolmanStatus Status, ToolSpools Tools, IReadOnlyDictionary<int, Spool> Spools,
                                   FileFilament File, bool Stale = false)
{
    public bool MultiTool => Tools.Tools.Count > 1;

    /// <summary>The spool in the tool in use: its macro's, else Moonraker's active spool.</summary>
    public int? ActiveSpoolId => Tools.Active >= 0 && Tools.Tools[Tools.Active].SpoolId is int id ? id : Status.ActiveSpoolId;

    public Spool? ActiveSpool => ActiveSpoolId is int id ? Spools.GetValueOrDefault(id) : null;

    /// <summary>The spools in the printer (every tool's, and the active one), which the list leaves out.</summary>
    public IEnumerable<int> LoadedIds => Tools.Tools.Select(t => t.SpoolId).Append(Status.ActiveSpoolId).OfType<int>().Distinct();
}

/// <summary>
/// A spool against the print: grams it still needs (null: unknown, or not printing), and the file's
/// material if it isn't the spool's.
/// </summary>
public sealed record SpoolCheck(double? Needed, string? WrongMaterial)
{
    public static SpoolCheck None { get; } = new(null, null);
}

public static class Spools
{
    /// <summary>Under this many grams a spool is shown in amber.</summary>
    public const double LowGrams = 100;

    /// <summary>
    /// What the print still needs from the spool in this tool (tool: its index, -1 on a printer without
    /// tool macros), from the slicer's weights × the part still to print. Per tool when the file has
    /// weights for each; otherwise the whole print's weight goes to the tool in use. The material is
    /// compared loosely ("PLA+" is PLA).
    /// </summary>
    public static SpoolCheck Check(Spool spool, SpoolmanState state, int tool, PrinterStatus status)
    {
        if (status.State is not ("printing" or "paused") || status.Filename.Length == 0)
            return SpoolCheck.None;
        var file = state.File;
        bool inUse = !state.MultiTool || tool == state.Tools.Active;
        double? weight = state.MultiTool && file.Weights.Count > 1
            ? (tool >= 0 && tool < file.Weights.Count ? file.Weights[tool] : null)
            : inUse ? file.TotalWeight ?? (file.Weights.Count > 0 ? file.Weights.Sum() : null) : null;
        double? needed = weight is > 0 ? weight * (1 - Math.Clamp(status.Progress, 0, 1)) : null;
        string? type = state.MultiTool && file.Types.Count > 1
            ? (tool >= 0 && tool < file.Types.Count ? file.Types[tool] : null)
            : inUse ? file.Types.FirstOrDefault() : null;
        // A tool this print doesn't use: neither applies.
        if (state.MultiTool && file.Weights.Count > 1 && weight is not > 0)
            type = null;
        string? wrong = type != null && spool.Material.Length > 0 && !SameMaterial(type, spool.Material) ? type : null;
        return new SpoolCheck(needed, wrong);
    }

    /// <summary>"PLA+" and "PLA", or "petg" and "PETG", are the same; ASA and PLA aren't.</summary>
    public static bool SameMaterial(string a, string b)
    {
        static string Plain(string s) => new string(s.Where(char.IsLetterOrDigit).ToArray()).ToUpperInvariant();
        string x = Plain(a), y = Plain(b);
        return x.Length == 0 || y.Length == 0 || x.StartsWith(y, StringComparison.Ordinal) || y.StartsWith(x, StringComparison.Ordinal);
    }

    /// <summary>How many grams short the spool is for the rest of the print (null: enough, or unknown).</summary>
    public static double? Short(Spool spool, SpoolCheck check) =>
        check.Needed is double needed && spool.Remaining is double left && needed > left ? needed - left : null;

    public static string Grams(double grams) => $"{Round(grams):0} g";

    public static string Metres(double millimetres) => $"{Round(millimetres / 1000):0} m";

    public static string Percent(double fraction) => $"{Round(fraction * 100):0}%";

    private static double Round(double value) => Math.Round(Math.Max(value, 0), MidpointRounding.AwayFromZero);

    /// <summary>The SPOOL tile: what's left on the spool in use, and its material and percentage, or how short it is.</summary>
    public static StatusText.Tile Tile(SpoolmanState? state, PrinterStatus status)
    {
        if (state == null)
            return new("—", "");
        if (state.ActiveSpool is not { } spool)
            return new("—", state.ActiveSpoolId == null ? "none picked" : "");
        string value = spool.Remaining is double left ? Grams(left) : "?";
        var check = Check(spool, state, state.MultiTool ? state.Tools.Active : -1, status);
        if (Short(spool, check) is double missing)
            return new(value, $"{Grams(missing)} short", Highlight: true);
        if (check.WrongMaterial != null)
            return new(value, $"sliced for {check.WrongMaterial}", Highlight: true);
        string detail = string.Join(" · ", new[] { spool.Material, spool.Fraction is double f ? Percent(f) : "" }.Where(s => s.Length > 0));
        return new(value, detail, Highlight: spool.Remaining is < LowGrams);
    }

    /// <summary>"used yesterday", "used 2 weeks ago"; "not used yet".</summary>
    public static string Used(DateTimeOffset? time, DateTimeOffset? now = null)
    {
        if (time is not { } t)
            return "not used yet";
        var days = ((now ?? DateTimeOffset.Now).ToLocalTime().Date - t.ToLocalTime().Date).TotalDays;
        return days < 1 ? "used today"
            : days < 2 ? "used yesterday"
            : days < 14 ? $"used {(int)days} days ago"
            : days < 60 ? $"used {(int)(days / 7)} weeks ago"
            : days < 730 ? $"used {(int)(days / 30.4)} months ago"
            : $"used {(int)(days / 365)} years ago";
    }
}

/// <summary>
/// Fetches the printer's spools for the panel and the SPOOL tile, which share it: whichever asks first
/// fetches, and the other gets the same answer if it's recent. Only asked while the popup shows.
/// </summary>
public sealed class SpoolmanSource
{
    private static readonly ConditionalWeakTable<IMoonrakerApi, SpoolmanSource> ForPrinter = new();

    public static SpoolmanSource For(IMoonrakerApi printer) => ForPrinter.GetValue(printer, p => new SpoolmanSource(new SpoolmanApi(p)));

    public SpoolmanApi Api { get; }
    /// <summary>The last state fetched, or null before the first.</summary>
    public SpoolmanState? State { get; private set; }
    /// <summary>A new state was fetched (on a background thread).</summary>
    public event Action? Changed;

    private readonly SemaphoreSlim _busy = new(1, 1);
    private DateTime _fetched = DateTime.MinValue;

    public SpoolmanSource(SpoolmanApi api) => Api = api;

    /// <summary>
    /// The state, fetched again unless it's newer than maxAge. Throws if the printer can't be reached;
    /// if only Spoolman isn't answering, the spools are the last ones known, marked stale.
    /// </summary>
    public async Task<SpoolmanState> GetAsync(string filename, TimeSpan maxAge, CancellationToken ct)
    {
        await _busy.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (State != null && DateTime.UtcNow - _fetched < maxAge)
                return State;
            var status = await Api.GetStatusAsync(ct).ConfigureAwait(false);
            var tools = await Api.GetToolsAsync(ct).ConfigureAwait(false);
            // Several tools on one extruder (a filament changer): the one in use holds the active spool.
            if (tools.Active < 0 && tools.Tools.Count > 1 && status.ActiveSpoolId is int active)
                tools = tools with { Active = tools.Tools.ToList().FindIndex(t => t.SpoolId == active) };
            FileFilament file;
            try { file = await Api.GetFileFilamentAsync(filename, ct).ConfigureAwait(false); }
            catch (Exception) when (!ct.IsCancellationRequested) { file = FileFilament.None; } // no slicer data: no checks
            var ids = tools.Tools.Select(t => t.SpoolId).Append(status.ActiveSpoolId).OfType<int>().Distinct().ToList();
            var spools = new Dictionary<int, Spool>();
            bool stale = !status.Connected;
            if (!stale)
            {
                try
                {
                    foreach (int id in ids)
                    {
                        try { spools[id] = await Api.GetSpoolAsync(id, ct).ConfigureAwait(false); }
                        catch (SpoolmanException e) when (e.StatusCode == 404) { } // deleted in Spoolman: shown as none
                    }
                }
                catch (SpoolmanException) { stale = true; }
            }
            if (stale && State != null)
                foreach (int id in ids)
                    if (!spools.ContainsKey(id) && State.Spools.TryGetValue(id, out var known))
                        spools[id] = known;
            var state = new SpoolmanState(status, tools, spools, file, stale);
            State = state;
            _fetched = DateTime.UtcNow;
            Changed?.Invoke();
            return state;
        }
        finally { _busy.Release(); }
    }
}
