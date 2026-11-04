using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace KlipperTrayIcon.Printers;

/// <summary>The printer said no to G-code, with Klipper's message ("Extrude below minimum temp").</summary>
public sealed class GcodeException(string message) : Exception(message);

/// <summary>
/// Sending G-code through Moonraker, for modules (Spoolman's Load and Unload, the console's command box),
/// and reading what it printed in the console.
/// </summary>
public static class Gcode
{
    /// <summary>
    /// Sends G-code and waits until it's done (a G28 or a heating macro can take minutes). Nothing
    /// is read back: Klipper's answer, or its error, shows in the console. Throws if the printer
    /// can't be reached, or <see cref="MoonrakerHttpException"/> if Klipper refused it (400).
    /// </summary>
    public static async Task SendGcodeAsync(this IMoonrakerApi printer, string script, CancellationToken ct)
    {
        using var _ = await printer.PostJsonAsync("/printer/gcode/script?script=" + Uri.EscapeDataString(script), null, ct,
                                                  TimeSpan.FromMinutes(10));
    }

    /// <summary>
    /// Runs G-code (a load or unload macro) and waits until it's done: heating can take minutes. It's
    /// judged by what it printed in the console while it ran, too: macros often report a failure with
    /// RESPOND TYPE=error ("!! …") rather than an error. Throws <see cref="GcodeException"/> with that
    /// message if it fails; otherwise returns its last message (RESPOND MSG="…"), or null.
    /// </summary>
    public static async Task<string?> RunGcodeAsync(this IMoonrakerApi printer, string script, CancellationToken ct)
    {
        // Where the console is now: the run's own lines come after it.
        double? mark = null;
        try { mark = (await ConsoleAsync(printer, 1, ct)).Select(e => e.Time).DefaultIfEmpty(0).Max(); }
        catch (Exception) when (!ct.IsCancellationRequested) { } // can't tell what it said, then
        bool refused = false;
        // The answer's body isn't passed on; Klipper's error is in the console.
        try { await printer.SendGcodeAsync(script, ct); }
        catch (MoonrakerHttpException e) when ((int)e.StatusCode == 400) { refused = true; }
        (string? error, string? message) = (null, null);
        if (mark != null || refused)
        {
            try { (error, message) = Said(await ConsoleAsync(printer, 1000, ct), mark); }
            catch (Exception) when (!ct.IsCancellationRequested) { }
        }
        if (refused || error != null)
            throw new GcodeException(error ?? $"{script} failed");
        return message;
    }

    private static async Task<List<(string Message, double Time)>> ConsoleAsync(IMoonrakerApi printer, int count, CancellationToken ct)
    {
        using var doc = await printer.GetJsonAsync($"/server/gcode_store?count={count}", ct, TimeSpan.FromSeconds(3));
        var store = doc.RootElement.Prop("result").Prop("gcode_store");
        return store.ValueKind != JsonValueKind.Array ? []
            : store.EnumerateArray().Select(e => (e.Str("message"), e.Num("time"))).ToList();
    }

    /// <summary>
    /// What G-code said in the console after the mark (all of it without one): its last error, first
    /// line only ("!! Must home axis first" → "Must home axis first"), and its last other message
    /// ("echo: Filament free: pull it out" → "Filament free: pull it out"); null for none.
    /// </summary>
    internal static (string? Error, string? Message) Said(IEnumerable<(string Message, double Time)> console, double? mark)
    {
        string? error = null, message = null;
        foreach (var (text, time) in console)
        {
            if (mark is double since && time <= since)
                continue;
            string line = text.Trim().Split('\n')[0].Trim();
            if (line.StartsWith("!!", StringComparison.Ordinal))
                error = line.TrimStart('!', ' ');
            else if (line.StartsWith("echo:", StringComparison.Ordinal))
                message = line["echo:".Length..].Trim();
            else if (line.StartsWith("//", StringComparison.Ordinal))
                message = line.TrimStart('/', ' ');
        }
        return (error is { Length: > 0 } ? error : null, message is { Length: > 0 } ? message : null);
    }
}
