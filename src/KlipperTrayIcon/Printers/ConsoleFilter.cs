using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace KlipperTrayIcon.Printers;

/// <summary>
/// Which console lines to hide, exactly as Mainsail (v2.19) does, so the popup
/// shows what Mainsail shows. Mainsail keeps its console settings in Moonraker's
/// database (namespace "mainsail", key "console"); see Mainsail's
/// src/store/gui/console/getters.ts (the rules), src/store/variables.ts
/// (timelapse rules) and src/store/server/actions.ts (applying them).
/// </summary>
public sealed class ConsoleFilter
{
    /// <summary>"Hide temperatures" (Mainsail's hideWaitTemperatures).</summary>
    internal const string WaitTemperatures = @"^(?:ok\s+)?(B|C|T\d*):";

    /// <summary>"Hide timelapse" (Mainsail's hideTlCommands, timelapseConsoleFilters).</summary>
    internal static readonly string[] TimelapseCommands =
    [
        "^_TIMELAPSE_NEW_FRAME",
        "^TIMELAPSE_TAKE_FRAME",
        "^TIMELAPSE_RENDER",
        "^_SET_TIMELAPSE_SETUP",
        "^HYPERLAPSE ACTION=",
        "^SET_GCODE_VARIABLE MACRO=TIMELAPSE_",
    ];

    /// <summary>Hides nothing (printers without a Klipper console).</summary>
    public static ConsoleFilter None { get; } = new([], null);

    /// <summary>Mainsail's defaults: temperatures and timelapse hidden, no custom filters.</summary>
    public static ConsoleFilter MainsailDefaults { get; } = FromMainsail(default);

    private readonly List<Regex> _rules;

    private ConsoleFilter(IReadOnlyList<string> rules, double? clearedSince)
    {
        Rules = rules;
        ClearedSince = clearedSince;
        _rules = rules.Select(Compile).OfType<Regex>().ToList();
    }

    /// <summary>The rules, in Mainsail's order.</summary>
    public IReadOnlyList<string> Rules { get; }

    /// <summary>Mainsail's "clear console": hides lines before this (Unix time in ms).</summary>
    public double? ClearedSince { get; }

    /// <summary>Same rules and clear time: no need to reload the console.</summary>
    public bool SameAs(ConsoleFilter other) => ClearedSince == other.ClearedSince && Rules.SequenceEqual(other.Rules);

    /// <summary>From Mainsail's "console" settings (default: none saved, so Mainsail's defaults).</summary>
    public static ConsoleFilter FromMainsail(JsonElement console)
    {
        var rules = new List<string>();
        // Missing settings mean Mainsail's defaults (both on).
        if (console.Prop("hideWaitTemperatures").ValueKind != JsonValueKind.False)
            rules.Add(WaitTemperatures);
        if (console.Prop("hideTlCommands").ValueKind != JsonValueKind.False)
            rules.AddRange(TimelapseCommands);
        var custom = console.Prop("consolefilters");
        if (custom.ValueKind == JsonValueKind.Object)
            foreach (var filter in custom.EnumerateObject())
                if (filter.Value.Prop("bool").ValueKind == JsonValueKind.True)
                    rules.AddRange(filter.Value.Str("regex").Split('\n').Where(rule => rule != ""));
        return new ConsoleFilter(rules, console.Opt("cleared_since"));
    }

    /// <summary>
    /// Mainsail's rules are JavaScript regular expressions (no flags). Compile
    /// them with ECMAScript behaviour where .NET allows it; a rule that doesn't
    /// compile is skipped, as Mainsail skips it.
    /// </summary>
    private static Regex? Compile(string rule)
    {
        foreach (var options in new[] { RegexOptions.ECMAScript, RegexOptions.None })
        {
            try { return new Regex(rule, options, TimeSpan.FromMilliseconds(50)); }
            catch (ArgumentException) { }
        }
        return null;
    }

    /// <summary>Whether Mainsail would show this line.</summary>
    public bool Shows(ConsoleEntry entry)
    {
        if (ClearedSince is double cleared && entry.Time * 1000 < cleared)
            return false;
        foreach (var rule in _rules)
        {
            try
            {
                if (rule.IsMatch(entry.Message))
                    return false;
            }
            catch (RegexMatchTimeoutException) { } // a runaway rule: treat as no match
        }
        return true;
    }
}
