using System;
using System.Collections.Generic;
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
    public static List<string> Lines(PrinterStatus s, DateTime? now = null)
    {
        string label = Label(s.State);
        var lines = new List<string>();
        if (s.State is "printing" or "paused")
        {
            lines.Add($"{label} · {(int)(s.Progress * 100)}%");
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

    /// <summary>Nozzle, bed, progress, layer and time-left tiles; "—" for anything unknown.</summary>
    public static Tile[] StatsTiles(PrinterStatus s, DateTime? now = null)
    {
        const string none = "—";
        var d = s.State == "offline" ? null : s.Details;
        bool active = s.State is "printing" or "paused";

        Tile Heater(Heater? h) => h == null ? new(none, "")
            : h.Target > 0
                // Highlighted while it's still heating up.
                ? new($"{h.Temperature:0}°", $"of {h.Target:0}°", h.Temperature < h.Target - 2)
                : new($"{h.Temperature:0}°", "off");

        var progress = active || s.State == "complete" ? new Tile($"{(int)(s.Progress * 100)}%", Label(s.State)) : new Tile(none, Label(s.State));

        Tile layer = new(none, "");
        if (active && d != null)
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

        return [Heater(d?.Nozzle), Heater(d?.Bed), progress, layer, time];
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
