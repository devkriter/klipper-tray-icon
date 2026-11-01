using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using KlipperTrayIcon.Printers;

namespace KlipperTrayIcon;

/// <summary>Turns a PrinterStatus into the words shown in the tooltip and menu.</summary>
public static partial class StatusText
{
    private static readonly Dictionary<string, string> Labels = new()
    {
        ["printing"] = "Printing",
        ["paused"] = "Paused",
        ["complete"] = "Print complete",
        ["cancelled"] = "Print cancelled",
        ["error"] = "Printer error",
        ["standby"] = "Idle",
        ["offline"] = "Offline",
    };

    public static string Label(string state) =>
        Labels.TryGetValue(state, out var label) ? label
        : state.Length == 0 ? "Unknown" : char.ToUpperInvariant(state[0]) + state[1..];

    /// <summary>Headline plus detail lines, e.g. "Printing · 45%", "1h 5m left · done 14:32 (slicer)", "Benchy.gcode".</summary>
    // How PrinterStatus.Halted starts.
    public const string ShutDown = "Klipper has shut down", CouldntStart = "Klipper couldn't start";

    public static List<string> Lines(PrinterStatus s, DateTime? now = null)
    {
        string label = Label(s.State);
        var lines = new List<string>();
        if (s.Halted != null)
        {
            // Klipper needs a restart: that's the news, whatever it was doing.
            lines.Add(s.Halted.StartsWith(CouldntStart) ? "Couldn't start" : "Shut down");
            lines.Add(s.Halted);
            return lines;
        }
        if (s.State is "printing" or "paused")
        {
            lines.Add(s.Preparing ? "Preparing to print" : $"{label} · {(int)(s.Progress * 100)}%");
            if (s.State == "printing" && s.TimeLeftSeconds > 0)
            {
                var eta = (now ?? DateTime.Now).AddSeconds(s.TimeLeftSeconds);
                string source = s.EtaSource == "Slicer" ? "slicer" : "est.";
                lines.Add($"{FormatDuration(s.TimeLeftSeconds)} left · done {eta:HH:mm} ({source})");
            }
            if (s.Filename.Length > 0)
                lines.Add(s.Filename);
            return lines;
        }
        lines.Add(label);
        if (s.State is "complete" or "cancelled" && s.Filename.Length > 0)
            lines.Add(s.Filename);
        else if (s.Message.Length > 0)
            lines.Add(s.Message);
        return lines;
    }

    public static string FormatDuration(double seconds)
    {
        if (seconds < 0) return "?";
        long total = (long)seconds;
        long h = total / 3600, m = total % 3600 / 60;
        return h > 0 ? $"{h}h {m}m" : $"{m}m";
    }

    /// <summary>One tile of the stats strip: a big value with a smaller line under it.</summary>
    public sealed record Tile(string Value, string Detail, bool Highlight = false);

    // ---- stats strip ---------------------------------------------------------------

    public const int MaxStatsTiles = 6;
    public const string Nozzle = "nozzle", Bed = "bed", Progress = "progress", Layer = "layer", TimeLeft = "time_left";

    /// <summary>Nozzle, bed, the chamber if the printer has a sensor for it, then progress, layer and time left.</summary>
    public static IReadOnlyList<string> DefaultTiles(string? chamber) =>
        string.IsNullOrEmpty(chamber) ? [Nozzle, Bed, Progress, Layer, TimeLeft] : [Nozzle, Bed, chamber, Progress, Layer, TimeLeft];

    /// <summary>A Klipper sensor ("temperature_fan chamber"), as opposed to a built-in tile; nozzles and the bed have their own.</summary>
    public static bool IsSensorTile(string tile) =>
        tile.Contains(' ') && !tile.StartsWith("extruder") && tile != "heater_bed";

    /// <summary>"chamber" for "temperature_fan chamber".</summary>
    public static string SensorName(string tile) => tile[(tile.IndexOf(' ') + 1)..];

    /// <summary>The tile's label in the strip: "NOZZLE", "TIME LEFT", "CHAMBER", "BEACON COIL".</summary>
    public static string TileLabel(string tile) => tile switch
    {
        Nozzle => "NOZZLE",
        Bed => "BED",
        Progress => "PROGRESS",
        Layer => "LAYER",
        TimeLeft => "TIME LEFT",
        _ => SensorName(tile).Replace('_', ' ').ToUpperInvariant(),
    };

    /// <summary>The labels for StatsTiles' tiles.</summary>
    public static string[] StatsLabels(PrinterStatus s, IReadOnlyList<string>? tiles = null)
    {
        tiles ??= s.Details?.Tiles ?? DefaultTiles(null);
        // Printers with several extruders: which one the nozzle tile shows, e.g. "NOZZLE T2".
        string? tool = s.State == "offline" ? null : s.Details?.Tool;
        return tiles.Select(t => t == Nozzle && tool != null ? $"NOZZLE {tool}" : TileLabel(t)).ToArray();
    }

    /// <summary>
    /// The stats strip: by default nozzle, bed, (chamber,) progress, layer and time left, or the
    /// tiles chosen in Settings. "—" for anything the printer doesn't report.
    /// </summary>
    public static Tile[] StatsTiles(PrinterStatus s, DateTime? now = null, IReadOnlyList<string>? tiles = null)
    {
        tiles ??= s.Details?.Tiles ?? DefaultTiles(null);
        const string none = "—";
        var d = s.State == "offline" ? null : s.Details;
        bool active = s.State is "printing" or "paused";

        Tile Heater(Heater? h) => h == null ? new(none, "")
            : !h.Heats
                // A plain sensor: no target.
                ? new($"{h.Temperature:0}°", "")
            : h.Target > 0
                // Highlighted while it's still heating up.
                ? new($"{h.Temperature:0}°", $"of {h.Target:0}°", h.Temperature < h.Target - 2)
                : new($"{h.Temperature:0}°", "off");

        var progress = active || s.State == "complete" ? new Tile($"{(int)(s.Progress * 100)}%", s.Preparing ? "Preparing" : Label(s.State)) : new Tile(none, Label(s.State));

        Tile layer = new(none, "");
        if (active && d != null && !s.Preparing) // while preparing, Z is wherever the start macro left it
        {
            if (d.Layer is int current)
                layer = new(current.ToString(), d.TotalLayers is int total ? $"of {total}" : "");
            else
                layer = new($"{d.Z:0.0}", "mm (Z)");
        }

        Tile time = new(none, "");
        if (s.State == "printing" && s.TimeLeftSeconds > 0)
        {
            var clock = now ?? DateTime.Now;
            var eta = clock.AddSeconds(s.TimeLeftSeconds);
            time = new(FormatDuration(s.TimeLeftSeconds), eta.Date == clock.Date ? $"done {eta:HH:mm}" : $"{eta:ddd HH:mm}");
        }
        else if (s.State == "paused")
            time = new(none, "paused");

        return tiles.Select(t => t switch
        {
            Nozzle => Heater(d?.Nozzle),
            Bed => Heater(d?.Bed),
            Progress => progress,
            Layer => layer,
            TimeLeft => time,
            _ => Heater(d?.Sensors?.GetValueOrDefault(t)),
        }).ToArray();
    }

    /// <summary>Keeps the end of the text (file names differ at the end).</summary>
    public static string Shorten(string text, int limit) =>
        text.Length <= limit ? text : "…" + text[^(limit - 1)..];

    /// <summary>
    /// Klipper's once-a-second heating report, e.g. "B:109.3 /110.0 T0:230.6 /250.0"
    /// (also T:, T1:, C: chamber, optionally prefixed with "ok").
    /// </summary>
    [GeneratedRegex(@"^(?:ok\s+)?(?:[A-Z]\d*:\s*-?\d+(?:\.\d+)?\s*/\s*-?\d+(?:\.\d+)?\s*)+$")]
    public static partial Regex TemperatureReport();
}
