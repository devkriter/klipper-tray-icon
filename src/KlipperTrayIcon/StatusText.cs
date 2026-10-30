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
