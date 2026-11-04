using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace KlipperTrayIcon.Modules.Commands;

/// <summary>
/// The commands sent to one printer, the last <see cref="Max"/>, kept in a file next to config.json
/// with every printer's (by address). Steps back through them for Up and Down in the empty box.
/// </summary>
public sealed class CommandHistory
{
    public const int Max = 100;

    /// <summary>The file, next to config.json.</summary>
    public static string DefaultPath => Path.Combine(ConfigStore.AppDir, "command_history.json");

    private readonly string _path;
    private readonly string _printer;
    private readonly List<string> _sent; // oldest first
    // Where Up and Down are in Recent (-1: not stepping, the box is the user's own).
    private int _step = -1;

    public CommandHistory(string path, string printer)
    {
        (_path, _printer) = (path, printer);
        _sent = ReadAll(path).GetValueOrDefault(printer) ?? [];
    }

    /// <summary>Every command kept, oldest first.</summary>
    public IReadOnlyList<string> All => _sent;

    /// <summary>The commands sent, newest first, each once.</summary>
    public List<string> Recent(int max = Max) =>
        Enumerable.Reverse(_sent).Distinct(StringComparer.Ordinal).Take(max).ToList();

    /// <summary>Adds a command sent, and saves the file (a failure to save only loses the history).</summary>
    public void Add(string command)
    {
        command = command.Trim();
        _step = -1;
        if (command.Length == 0)
            return;
        _sent.Add(command);
        if (_sent.Count > Max)
            _sent.RemoveRange(0, _sent.Count - Max);
        try
        {
            var all = ReadAll(_path);
            all[_printer] = _sent;
            string tmp = _path + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(all, ConfigStore.JsonOptions));
            File.Move(tmp, _path, overwrite: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }

    /// <summary>Up: the command before the one shown (the newest at first), or null at the oldest.</summary>
    public string? Older()
    {
        var recent = Recent();
        if (_step + 1 >= recent.Count)
            return null;
        return recent[++_step];
    }

    /// <summary>Down: the command after the one shown, "" past the newest (the empty box again), or null when not stepping.</summary>
    public string? Newer()
    {
        if (_step < 0)
            return null;
        return --_step < 0 ? "" : Recent()[_step];
    }

    /// <summary>The user typed: Up starts from the newest again.</summary>
    public void StopStepping() => _step = -1;

    private static Dictionary<string, List<string>> ReadAll(string path)
    {
        try
        {
            if (File.Exists(path))
                return JsonSerializer.Deserialize<Dictionary<string, List<string>>>(File.ReadAllText(path), ConfigStore.JsonOptions) ?? [];
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException) { }
        return [];
    }
}
