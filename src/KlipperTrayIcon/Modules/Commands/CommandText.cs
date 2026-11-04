using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace KlipperTrayIcon.Modules.Commands;

/// <summary>A command the printer knows (from its G-code help), where what was typed is found in its name.</summary>
public sealed record Suggestion(string Name, string Description, int MatchStart, int MatchLength);

/// <summary>A pinned command: its name in the dropdown, and what it sends (with {240} for a value to fill in).</summary>
public sealed record Pin(string Name, string Command)
{
    private static readonly Regex Value = new(@"\{([^{}]*)\}");

    /// <summary>Whether it asks for a value ({240}) before it's sent.</summary>
    public bool HasValue => Value.IsMatch(Command);

    /// <summary>
    /// The command with its values filled in with their defaults ("M104 S{240}" → "M104 S240"), and
    /// where the first one is, to select it for typing over.
    /// </summary>
    public (string Text, int Start, int Length) Fill()
    {
        var first = Value.Match(Command);
        if (!first.Success)
            return (Command, Command.Length, 0);
        // The first value is the first one replaced, so it starts where its brace did.
        return (Value.Replace(Command, m => m.Groups[1].Value), first.Index, first.Groups[1].Length);
    }

    /// <summary>The pins in the module's setting: one "Name: command" per line (a line without a name is its own name).</summary>
    public static List<Pin> Parse(string lines) =>
        lines.Replace("\r\n", "\n").Split('\n')
             .Select(l => l.Trim())
             .Where(l => l.Length > 0)
             .Select(l =>
             {
                 int colon = l.IndexOf(':');
                 string name = colon > 0 ? l[..colon].Trim() : "", command = colon > 0 ? l[(colon + 1)..].Trim() : l;
                 return command.Length == 0 ? new Pin(name, name) : new Pin(name.Length > 0 ? name : command, command);
             })
             .ToList();

    /// <summary>The pins as the setting saves them, one per line.</summary>
    public static string Format(IEnumerable<Pin> pins) => string.Join("\n", pins.Select(p => $"{p.Name}: {p.Command}"));

    /// <summary>A pin made from a command the user sent, named after it.</summary>
    public static Pin From(string command) => new(command.Replace(":", "").Trim(), command.Trim());
}

/// <summary>What the command box does with text: suggestions, "did you mean", capitals.</summary>
public static class CommandText
{
    /// <summary>The first word: the command ("M104" in "M104 S240").</summary>
    public static string Word(string text)
    {
        text = text.TrimStart();
        int space = text.IndexOfAny([' ', '\t']);
        return space < 0 ? text : text[..space];
    }

    /// <summary>The command word in capitals, the rest as typed ("set_led led=x" → "SET_LED led=x"). Same length.</summary>
    public static string Capitalize(string text)
    {
        int start = text.Length - text.TrimStart().Length;
        int end = start + Word(text).Length;
        return text[..start] + text[start..end].ToUpperInvariant() + text[end..];
    }

    /// <summary>
    /// The commands whose name has the word in it, anywhere: those starting with it first, then by where
    /// it's found, then shortest. Empty for an empty word, or once a space is typed (the command is done).
    /// </summary>
    public static List<Suggestion> Suggest(IReadOnlyDictionary<string, string> help, string text, int max = 8)
    {
        string word = Word(text);
        if (word.Length == 0 || text.TrimStart().Length > word.Length)
            return [];
        return help.Select(c => (c, At: c.Key.IndexOf(word, StringComparison.OrdinalIgnoreCase)))
                   .Where(c => c.At >= 0 && !c.c.Key.Equals(word, StringComparison.OrdinalIgnoreCase))
                   .OrderBy(c => c.At > 0)
                   .ThenBy(c => c.At)
                   .ThenBy(c => c.c.Key.Length)
                   .ThenBy(c => c.c.Key, StringComparer.Ordinal)
                   .Take(max)
                   .Select(c => new Suggestion(c.c.Key, c.c.Value, c.At, word.Length))
                   .ToList();
    }

    /// <summary>The rest of the top suggestion after what's typed, shown dim in the box ("BED_M" → "ESH_CALIBRATE"), or "".</summary>
    public static string Ghost(string text, IReadOnlyList<Suggestion> suggestions) =>
        suggestions.Count > 0 && suggestions[0].MatchStart == 0 && text.Length > 0 && !char.IsWhiteSpace(text[0])
            ? suggestions[0].Name[text.Length..] : "";

    /// <summary>
    /// When the command word isn't a command the printer knows, nor part of one: the closest one by
    /// edit distance ("G2B" → "G28"), or "" when nothing is close. Null when it's fine (or there's no list).
    /// </summary>
    public static string? DidYouMean(IReadOnlyDictionary<string, string> help, string text)
    {
        string word = Word(text);
        if (help.Count == 0 || word.Length == 0
            || help.Keys.Any(k => k.Contains(word, StringComparison.OrdinalIgnoreCase)))
            return null;
        string upper = word.ToUpperInvariant();
        var (closest, distance) = help.Keys.Select(k => (k, Distance(upper, k.ToUpperInvariant())))
                                          .OrderBy(c => c.Item2).ThenBy(c => c.k.Length).ThenBy(c => c.k, StringComparer.Ordinal)
                                          .First();
        // A third of it wrong, at most (one letter in a short G-code).
        return distance <= Math.Max(1, word.Length / 3) ? closest : "";
    }

    /// <summary>The text with its command word swapped for another ("G2B X" → "G28 X").</summary>
    public static string ReplaceWord(string text, string command)
    {
        int start = text.Length - text.TrimStart().Length;
        return text[..start] + command + text[(start + Word(text).Length)..];
    }

    /// <summary>Levenshtein distance: the fewest letters added, removed or changed to turn one into the other.</summary>
    public static int Distance(string a, string b)
    {
        var row = Enumerable.Range(0, b.Length + 1).ToArray();
        for (int i = 1; i <= a.Length; i++)
        {
            int diagonal = row[0];
            row[0] = i;
            for (int j = 1; j <= b.Length; j++)
            {
                int above = row[j];
                row[j] = Math.Min(Math.Min(row[j] + 1, row[j - 1] + 1), diagonal + (a[i - 1] == b[j - 1] ? 0 : 1));
                diagonal = above;
            }
        }
        return row[b.Length];
    }

    /// <summary>Moonraker's /printer/gcode/help: every command and macro, by name in capitals, with its description.</summary>
    public static Dictionary<string, string> ReadHelp(JsonElement result)
    {
        var help = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (result.ValueKind == JsonValueKind.Object)
            foreach (var command in result.EnumerateObject())
                help[command.Name.ToUpperInvariant()] = command.Value.ValueKind == JsonValueKind.String ? command.Value.GetString()!.Trim() : "";
        return help;
    }
}
