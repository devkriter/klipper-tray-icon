using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using KlipperTrayIcon.Printers;

namespace KlipperTrayIcon.Modules.Spoolman;

/// <summary>
/// A spool in Spoolman. Weights are grams, Length is millimetres; null when Spoolman doesn't know.
/// Colors are the filament's hex colours ("C8312B"), several for a multi-colour filament.
/// </summary>
public sealed record Spool(
    int Id,
    string Name,
    string Vendor,
    string Material,
    IReadOnlyList<string> Colors,
    double? Remaining,
    double? Initial,
    double? Length,
    string Location = "",
    DateTimeOffset? LastUsed = null,
    DateTimeOffset? FirstUsed = null,
    double? ExtruderTemp = null,
    double? BedTemp = null,
    string Lot = "",
    double? Price = null,
    string Comment = "",
    bool Archived = false)
{
    /// <summary>What's left of the starting weight, 0 to 1; null without a starting weight.</summary>
    public double? Fraction => Remaining is double left && Initial is > 0 and var start ? Math.Clamp(left / start, 0, 1) : null;

    /// <summary>"PolyLite ASA Red", or the material when the filament has no name.</summary>
    public string Title => Name.Length > 0 ? (Vendor.Length > 0 && !Name.StartsWith(Vendor, StringComparison.OrdinalIgnoreCase) ? $"{Vendor} {Name}" : Name)
        : Material.Length > 0 ? Material : $"Spool {Id}";

    // Equal when shown the same (records compare lists by reference).
    public bool Equals(Spool? other) => other is not null && Id == other.Id && Name == other.Name && Vendor == other.Vendor
        && Material == other.Material && Colors.SequenceEqual(other.Colors) && Remaining == other.Remaining && Initial == other.Initial
        && Length == other.Length && Location == other.Location && LastUsed == other.LastUsed && FirstUsed == other.FirstUsed
        && ExtruderTemp == other.ExtruderTemp && BedTemp == other.BedTemp && Lot == other.Lot && Price == other.Price
        && Comment == other.Comment && Archived == other.Archived;

    public override int GetHashCode() => HashCode.Combine(Id, Remaining, Name, LastUsed);
}

/// <summary>Moonraker's link to Spoolman: whether it's up, reports waiting for it, and the spool it counts usage for.</summary>
public sealed record SpoolmanStatus(bool Connected, int PendingReports, int? ActiveSpoolId);

/// <summary>A toolhead with a spool_id variable on its macro (T0, T1, …), as Mainsail and Fluidd use them.</summary>
public sealed record Tool(string Name, int? SpoolId);

/// <summary>
/// Toolheads with a spool each (empty on a printer without T0…Tn macros that hold a spool_id),
/// and which one is in use (an index into Tools, or -1).
/// </summary>
public sealed record ToolSpools(IReadOnlyList<Tool> Tools, int Active)
{
    public static ToolSpools None { get; } = new([], -1);
}

/// <summary>The slicer's filament data for a file: grams for the whole print, per tool if it says, and material(s).</summary>
public sealed record FileFilament(double? TotalWeight, IReadOnlyList<double> Weights, IReadOnlyList<string> Types)
{
    public static FileFilament None { get; } = new(null, [], []);
}

/// <summary>
/// The printer's macros that load and unload filament: LOAD_FILAMENT and UNLOAD_FILAMENT (as Mainsail,
/// Fluidd and KlipperScreen call them), per tool LOAD_FILAMENT_T1, and the tool parameter the plain
/// ones read, if any (TOOL, T or EXTRUDER). Macros are upper case.
/// </summary>
public sealed record FilamentMacros(IReadOnlySet<string> Macros, string? LoadParameter = null, string? UnloadParameter = null)
{
    public static FilamentMacros None { get; } = new(new HashSet<string>());

    public static string Name(bool load) => load ? "LOAD_FILAMENT" : "UNLOAD_FILAMENT";

    /// <summary>
    /// The G-code that loads (or unloads) a toolhead (its number; -1 on a printer with one), or null if
    /// the printer has no macro for it. A plain macro that takes no tool only works on the tool in use.
    /// Chosen: the user's own command instead (the module's setting), run as it is with {tool} as the
    /// toolhead's number; without {tool}, it too only works on the tool in use.
    /// </summary>
    public string? Script(bool load, int tool, bool inUse, string? chosen = null)
    {
        string name = Name(load);
        if (chosen != null && chosen.Length > 0 && !chosen.Equals(name, StringComparison.OrdinalIgnoreCase))
            return chosen.Contains("{tool}", StringComparison.Ordinal)
                ? chosen.Replace("{tool}", Math.Max(tool, 0).ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal)
                : tool < 0 || inUse ? chosen : null;
        if (tool < 0)
            return Macros.Contains(name) ? name : null;
        if (Macros.Contains($"{name}_T{tool}"))
            return $"{name}_T{tool}";
        if (!Macros.Contains(name))
            return null;
        return (load ? LoadParameter : UnloadParameter) switch
        {
            "EXTRUDER" => $"{name} EXTRUDER={(tool == 0 ? "extruder" : $"extruder{tool}")}",
            string parameter => $"{name} {parameter}={tool}",
            null => inUse ? name : null,
        };
    }

    /// <summary>The tool parameter a macro's G-code reads ("TOOL", "T" or "EXTRUDER"), or null.</summary>
    internal static string? ToolParameter(string gcode) =>
        new[] { "TOOL", "T", "EXTRUDER" }.FirstOrDefault(p =>
            Regex.IsMatch(gcode, $@"params\s*(\.\s*{p}\b|\[\s*['""]{p}['""]\s*\])"));
}

/// <summary>Spoolman said no (through Moonraker's proxy), or isn't answering. StatusCode is Spoolman's, e.g. 404.</summary>
public sealed class SpoolmanException(string message, int statusCode = 0) : Exception(message)
{
    public int StatusCode { get; } = statusCode;
}

/// <summary>
/// Spoolman, through Moonraker's [spoolman] component: Moonraker's status and active spool, and
/// Spoolman's spools through Moonraker's proxy, so no second address or key is needed. Per-tool
/// spools are the spool_id variables on the T0…Tn macros. Every call throws if the printer can't be
/// reached.
/// </summary>
public sealed class SpoolmanApi(IMoonrakerApi printer)
{
    private static readonly Regex ToolMacro = new(@"^gcode_macro (T(\d+))$", RegexOptions.IgnoreCase);

    // The printer's T0…Tn macros and extruders, looked up once (they change only with Klipper's config).
    private (List<(string Macro, string Tool, int Number)> Macros, HashSet<string> Extruders, HashSet<string> All)? _objects;
    private FilamentMacros? _filamentMacros;
    private string? _fileFor;
    private FileFilament? _file;
    private string? _currency;

    /// <summary>Whether this Moonraker has the [spoolman] component.</summary>
    public async Task<bool> HasSpoolmanAsync(CancellationToken ct)
    {
        using var doc = await printer.GetJsonAsync("/server/info", ct, TimeSpan.FromSeconds(3));
        var components = doc.RootElement.Prop("result").Prop("components");
        return components.ValueKind == JsonValueKind.Array
               && components.EnumerateArray().Any(c => c.GetString() == "spoolman");
    }

    public async Task<SpoolmanStatus> GetStatusAsync(CancellationToken ct)
    {
        using var doc = await printer.GetJsonAsync("/server/spoolman/status", ct, TimeSpan.FromSeconds(3));
        var result = doc.RootElement.Prop("result");
        var pending = result.Prop("pending_reports");
        return new SpoolmanStatus(result.Prop("spoolman_connected").ValueKind == JsonValueKind.True,
                                  pending.ValueKind == JsonValueKind.Array ? pending.GetArrayLength() : 0,
                                  result.Opt("spool_id") is double id ? (int)id : null);
    }

    /// <summary>Every spool that isn't archived, most recently used first (never used last).</summary>
    public async Task<IReadOnlyList<Spool>> GetSpoolsAsync(CancellationToken ct)
    {
        using var doc = await ProxyAsync("GET", "/v1/spool", "sort=last_used:desc", ct);
        var spools = doc.RootElement.Prop("result").Prop("response");
        if (spools.ValueKind != JsonValueKind.Array)
            return [];
        return spools.EnumerateArray().Select(ReadSpool)
            .Where(s => !s.Archived)
            .OrderByDescending(s => s.LastUsed ?? DateTimeOffset.MinValue)
            .ToList();
    }

    public async Task<Spool> GetSpoolAsync(int id, CancellationToken ct)
    {
        using var doc = await ProxyAsync("GET", $"/v1/spool/{id}", "", ct);
        return ReadSpool(doc.RootElement.Prop("result").Prop("response"));
    }

    /// <summary>Spoolman's currency ("EUR"), for prices; null if it isn't set. Asked once.</summary>
    public async Task<string?> GetCurrencyAsync(CancellationToken ct)
    {
        if (_currency != null)
            return _currency.Length > 0 ? _currency : null;
        try
        {
            using var doc = await ProxyAsync("GET", "/v1/setting/currency", "", ct);
            // Settings hold their value as JSON: "\"EUR\"".
            string value = doc.RootElement.Prop("result").Prop("response").Str("value");
            _currency = value.Length > 0 ? JsonSerializer.Deserialize<string>(value) ?? "" : "";
        }
        catch (Exception e) when (e is SpoolmanException or JsonException) { _currency = ""; }
        return _currency.Length > 0 ? _currency : null;
    }

    /// <summary>The toolheads with a spool_id variable on their T0…Tn macro, and which one is in use.</summary>
    public async Task<ToolSpools> GetToolsAsync(CancellationToken ct)
    {
        var (macros, extruders, _) = await ObjectsAsync(ct);
        if (macros.Count == 0)
            return ToolSpools.None;
        using var doc = await printer.GetJsonAsync(
            "/printer/objects/query?toolhead=extruder" + string.Concat(macros.Select(m => $"&{Uri.EscapeDataString(m.Macro)}=spool_id")),
            ct, TimeSpan.FromSeconds(3));
        return ReadTools(doc.RootElement.Prop("result").Prop("status"), macros, extruders);
    }

    private async Task<(List<(string Macro, string Tool, int Number)> Macros, HashSet<string> Extruders, HashSet<string> All)> ObjectsAsync(
        CancellationToken ct)
    {
        if (_objects == null)
        {
            using var list = await printer.GetJsonAsync("/printer/objects/list", ct, TimeSpan.FromSeconds(3));
            var objects = list.RootElement.Prop("result").Prop("objects");
            var names = objects.ValueKind == JsonValueKind.Array ? objects.EnumerateArray().Select(o => o.GetString() ?? "").ToList() : [];
            _objects = (names.Select(n => ToolMacro.Match(n))
                             .Where(m => m.Success)
                             .Select(m => (m.Value, m.Groups[1].Value.ToUpperInvariant(), int.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture)))
                             .OrderBy(m => m.Item3)
                             .ToList(),
                        names.Where(n => n.StartsWith("extruder", StringComparison.Ordinal) && !n.Contains(' ')).ToHashSet(),
                        names.ToHashSet());
        }
        return _objects.Value;
    }

    /// <summary>
    /// The printer's macros, upper case, for the module's settings: the ones about filament first,
    /// then the rest by name. Hidden ones (_NAME, as Mainsail hides them) are left out.
    /// </summary>
    public async Task<IReadOnlyList<string>> GetMacrosAsync(CancellationToken ct) =>
        MacroChoices((await ObjectsAsync(ct)).All);

    internal static List<string> MacroChoices(IEnumerable<string> objects) =>
        objects.Where(n => n.StartsWith("gcode_macro ", StringComparison.OrdinalIgnoreCase))
               .Select(n => n["gcode_macro ".Length..].Trim().ToUpperInvariant())
               .Where(n => n.Length > 0 && !n.StartsWith('_'))
               .Distinct()
               .OrderBy(n => n.Contains("FILAMENT", StringComparison.Ordinal) || n.Contains("LOAD", StringComparison.Ordinal) ? 0 : 1)
               .ThenBy(n => n, StringComparer.Ordinal)
               .ToList();

    /// <summary>
    /// The printer's load and unload macros. On a printer with several tools, the plain ones' G-code
    /// (from Klipper's config) says whether they take a tool. Looked up once.
    /// </summary>
    public async Task<FilamentMacros> GetFilamentMacrosAsync(CancellationToken ct)
    {
        if (_filamentMacros != null)
            return _filamentMacros;
        var (tools, _, all) = await ObjectsAsync(ct);
        var macros = all.Where(n => n.StartsWith("gcode_macro ", StringComparison.OrdinalIgnoreCase))
                        .Select(n => n["gcode_macro ".Length..].Trim().ToUpperInvariant())
                        .Where(n => n.Contains("LOAD_FILAMENT", StringComparison.Ordinal))
                        .ToHashSet();
        var found = new FilamentMacros(macros);
        if (tools.Count > 1 && (macros.Contains("LOAD_FILAMENT") || macros.Contains("UNLOAD_FILAMENT")))
        {
            using var doc = await printer.GetJsonAsync("/printer/objects/query?configfile=settings", ct, TimeSpan.FromSeconds(5));
            var settings = doc.RootElement.Prop("result").Prop("status").Prop("configfile").Prop("settings");
            found = found with
            {
                LoadParameter = FilamentMacros.ToolParameter(settings.Prop("gcode_macro load_filament").Str("gcode")),
                UnloadParameter = FilamentMacros.ToolParameter(settings.Prop("gcode_macro unload_filament").Str("gcode")),
            };
        }
        return _filamentMacros = found;
    }

    /// <summary>
    /// The tools whose macro has a spool_id. The one in use is the one on the active extruder
    /// (T1 on extruder1) when each tool has an extruder, as on a tool changer.
    /// </summary>
    internal static ToolSpools ReadTools(JsonElement status, IReadOnlyList<(string Macro, string Tool, int Number)> macros,
                                         ISet<string> extruders)
    {
        var tools = new List<(Tool Tool, int Number)>();
        foreach (var (macro, name, number) in macros)
        {
            var variables = status.Prop(macro);
            if (variables.Prop("spool_id").ValueKind == JsonValueKind.Undefined)
                continue; // a tool macro that doesn't track spools
            tools.Add((new Tool(name, variables.Opt("spool_id") is double id and > 0 ? (int)id : null), number));
        }
        string extruder = status.Prop("toolhead").Str("extruder");
        int active = -1;
        if (extruders.Count > 1 && extruder.StartsWith("extruder", StringComparison.Ordinal))
        {
            int number = extruder == "extruder" ? 0 : int.TryParse(extruder[8..], out int n) ? n : -1;
            active = tools.FindIndex(t => t.Number == number);
        }
        return new ToolSpools(tools.Select(t => t.Tool).ToList(), active);
    }

    /// <summary>The slicer's filament data for this G-code file (cached for the last file asked).</summary>
    public async Task<FileFilament> GetFileFilamentAsync(string filename, CancellationToken ct)
    {
        if (filename.Length == 0)
            return FileFilament.None;
        if (filename == _fileFor && _file != null)
            return _file;
        using var doc = await printer.GetJsonAsync("/server/files/metadata?filename=" + Uri.EscapeDataString(filename),
                                                   ct, TimeSpan.FromSeconds(3));
        var file = ReadFileFilament(doc.RootElement.Prop("result"));
        (_fileFor, _file) = (filename, file);
        return file;
    }

    internal static FileFilament ReadFileFilament(JsonElement meta)
    {
        var weights = meta.Prop("filament_weights");
        return new FileFilament(
            meta.Opt("filament_weight_total") is > 0 and var total ? total : null,
            weights.ValueKind == JsonValueKind.Array
                ? weights.EnumerateArray().Select(w => w.ValueKind == JsonValueKind.Number ? w.GetDouble() : 0).ToList() : [],
            ReadTypes(meta.Prop("filament_type")));
    }

    /// <summary>
    /// The file's materials, one per filament slot: "PLA;PLA;PETG", or (OrcaSlicer, through newer
    /// Moonraker) a JSON list in a string, '["ASA", "ASA", "ABS"]'.
    /// </summary>
    static List<string> ReadTypes(JsonElement types)
    {
        if (types.ValueKind == JsonValueKind.String && types.GetString()!.TrimStart().StartsWith('['))
        {
            try
            {
                using var list = JsonDocument.Parse(types.GetString()!);
                return ReadTypes(list.RootElement);
            }
            catch (JsonException) { }
        }
        var names = types.ValueKind switch
        {
            JsonValueKind.Array => types.EnumerateArray().Select(t => t.ValueKind == JsonValueKind.String ? t.GetString()! : ""),
            JsonValueKind.String => types.GetString()!.Split(';'),
            _ => []
        };
        return names.Select(t => t.Trim(' ', '"', '[', ']')).Where(t => t.Length > 0).ToList();
    }

    /// <summary>Spoolman's own page (from moonraker.conf's [spoolman] server), or null.</summary>
    public async Task<string?> GetSpoolmanUrlAsync(CancellationToken ct)
    {
        using var doc = await printer.GetJsonAsync("/server/config", ct, TimeSpan.FromSeconds(3));
        string url = doc.RootElement.Prop("result").Prop("config").Prop("spoolman").Str("server").Trim();
        return url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || url.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
            ? url : null;
    }

    /// <summary>The spool Moonraker counts usage for (null: none).</summary>
    public Task SetActiveSpoolAsync(int? id, CancellationToken ct) =>
        printer.SendAsync(HttpMethod.Post, "/server/spoolman/spool_id", new { spool_id = id }, ct);

    /// <summary>
    /// Puts a spool in a tool (null: takes it out): its macro's spool_id variable, as Mainsail and Fluidd
    /// set it, and Moonraker's active spool too if the tool is the one in use.
    /// </summary>
    public async Task SetToolSpoolAsync(string tool, int? id, bool active, CancellationToken ct)
    {
        string value = id is int n ? n.ToString(CultureInfo.InvariantCulture) : "None";
        string script = $"SET_GCODE_VARIABLE MACRO={tool} VARIABLE=spool_id VALUE={value}";
        await printer.SendAsync(HttpMethod.Post, "/printer/gcode/script?script=" + Uri.EscapeDataString(script), null, ct);
        if (active)
            await SetActiveSpoolAsync(id, ct);
    }

    /// <summary>A request to Spoolman through Moonraker. Throws <see cref="SpoolmanException"/> if Spoolman says no.</summary>
    private async Task<JsonDocument> ProxyAsync(string method, string path, string query, CancellationToken ct)
    {
        var doc = await printer.PostJsonAsync("/server/spoolman/proxy",
            new { request_method = method, path, query, use_v2_response = true }, ct, TimeSpan.FromSeconds(5));
        var error = doc.RootElement.Prop("result").Prop("error");
        if (error.ValueKind == JsonValueKind.Object)
        {
            string message = error.Str("message");
            int status = (int)error.Num("status_code");
            doc.Dispose();
            throw new SpoolmanException(message.Length > 0 ? message : "Spoolman isn't answering", status);
        }
        return doc;
    }

    internal static Spool ReadSpool(JsonElement s)
    {
        var filament = s.Prop("filament");
        string multi = filament.Str("multi_color_hexes");
        var colors = (multi.Length > 0 ? multi.Split(',') : [filament.Str("color_hex")])
            .Select(c => c.Trim().TrimStart('#'))
            .Where(c => c.Length is 6 or 8 && c.All(Uri.IsHexDigit))
            .ToList();
        return new Spool(
            (int)s.Num("id"),
            filament.Str("name").Trim(),
            filament.Prop("vendor").Str("name").Trim(),
            filament.Str("material").Trim(),
            colors,
            s.Opt("remaining_weight"),
            s.Opt("initial_weight") ?? filament.Opt("weight"),
            s.Opt("remaining_length"),
            s.Str("location").Trim(),
            Time(s.Str("last_used")),
            Time(s.Str("first_used")),
            filament.Opt("settings_extruder_temp"),
            filament.Opt("settings_bed_temp"),
            s.Str("lot_nr").Trim(),
            s.Opt("price") ?? filament.Opt("price"),
            s.Str("comment").Trim(),
            s.Prop("archived").ValueKind == JsonValueKind.True);
    }

    // Spoolman's times are UTC, sometimes without the Z.
    private static DateTimeOffset? Time(string value) =>
        DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture,
                                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var time) ? time : null;
}
