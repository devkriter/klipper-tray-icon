using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using KlipperTrayIcon.Printers;

namespace KlipperTrayIcon.Modules.Afc;

/// <summary>
/// A lane of an AFC unit: its sensors (Prep at the lane's entry, Load further in), how far its filament
/// is (to the hub, into the toolhead), and AFC's copy of its Spoolman spool. Color is "#00677F", or
/// empty when AFC doesn't know it. Weight is grams; it means something only with a spool.
/// </summary>
public sealed record AfcLane(string Name, string Unit, string Hub, string Map, bool Prep, bool Load, bool LoadedToHub,
                             bool ToolLoaded, string Status, int? SpoolId, string Color, string Filament, string Material,
                             double? Weight, double? InitialWeight)
{
    /// <summary>No filament in it: neither sensor sees any.</summary>
    public bool Empty => !Prep && !Load;

    /// <summary>The tool it maps to ("T0"), or its own name without one.</summary>
    public string Title => Map.Length > 0 ? Map.ToUpperInvariant() : Name;

    /// <summary>What's left of the spool, 0 to 1; null without a spool or a starting weight.</summary>
    public double? Fraction => SpoolId != null && Weight is double left && InitialWeight is > 0 and var start
        ? Math.Clamp(left / start, 0, 1) : null;
}

/// <summary>A hub, where the lanes meet, and its sensor. Sensor is the Klipper name of that sensor.</summary>
public sealed record AfcHub(string Name, bool State, string Sensor);

/// <summary>A buffer between the hub and the toolhead: "Trailing" or "Advancing" (State), and whether it's in use.</summary>
public sealed record AfcBuffer(string Name, string State, bool Enabled);

/// <summary>
/// The toolhead's end of the path: its sensors before the extruder gears (Pre, tool_start) and after
/// them (Post, tool_end), each null when it has none, with their Klipper names, and the lane loaded in it.
/// </summary>
public sealed record AfcExtruder(string Name, bool? Pre, bool? Post, string PreSensor, string PostSensor, string? LaneLoaded);

/// <summary>
/// Everything the AFC page and the LANE tile show: the units (their names), the lanes in AFC's order,
/// the hubs, buffers and extruders; AFC's own state ("Idle", "Loading"), the lane in the toolhead
/// (CurrentLoad) and the one it's busy with (CurrentLane, NextLane); the tool change count of the
/// print (Change of Changes); the error flag and AFC's last message; bypass (on, and whether the
/// printer has a bypass sensor at all).
/// </summary>
public sealed record AfcState(IReadOnlyList<string> Units, IReadOnlyList<AfcLane> Lanes, IReadOnlyList<AfcHub> Hubs,
                              IReadOnlyList<AfcBuffer> Buffers, IReadOnlyList<AfcExtruder> Extruders,
                              string CurrentState, string? CurrentLoad, string? CurrentLane, string? NextLane,
                              int Change, int Changes, bool Error, string Message, string MessageType,
                              bool Bypass, bool HasBypass)
{
    /// <summary>The lane in the toolhead, or null.</summary>
    public AfcLane? Loaded => Lane(CurrentLoad) ?? Lanes.FirstOrDefault(l => l.ToolLoaded)
                              ?? Lane(Extruders.FirstOrDefault()?.LaneLoaded);

    public AfcLane? Lane(string? name) => name == null ? null : Lanes.FirstOrDefault(l => l.Name == name);

    /// <summary>"NIGHTOWL": the units' names, as on the unit.</summary>
    public string UnitTitle => string.Join(" · ", Units.Select(u => u.ToUpperInvariant()));

    /// <summary>AFC is loading or unloading something (not idle).</summary>
    public bool Busy => CurrentState.Length > 0 && !CurrentState.Equals("Idle", StringComparison.OrdinalIgnoreCase);

    /// <summary>AFC's message, first line, when it's an error or a warning; else empty.</summary>
    public string Problem => MessageType is "error" or "warning" || Error ? Message.Split('\n')[0].Trim() : "";

    /// <summary>The lane AFC is busy with: the one it's loading, else the one it's unloading.</summary>
    public AfcLane? Working => CurrentState.StartsWith("Load", StringComparison.OrdinalIgnoreCase)
        ? Lane(NextLane) ?? Lane(CurrentLane) : Lane(CurrentLane) ?? Loaded;

    /// <summary>
    /// The page's header and the tile's line: "Change 3 of 12 · Loading T1" in a print's tool change,
    /// "Printing T1" while printing, "Loading T1" or AFC's own state otherwise.
    /// </summary>
    public string Describe(bool printing)
    {
        if (Error)
            return "Error";
        if (Bypass)
            return "Bypass";
        string doing = Busy
            ? CurrentState.StartsWith("Load", StringComparison.OrdinalIgnoreCase) || CurrentState.StartsWith("Unload", StringComparison.OrdinalIgnoreCase)
                ? $"{CurrentState} {Working?.Title}".Trim() : CurrentState
            : "";
        if (printing)
        {
            if (doing.Length > 0)
                return Changes > 0 ? $"Change {Change} of {Changes} · {doing}" : doing;
            return Loaded is { } lane ? $"Printing {lane.Title}" : "Printing";
        }
        return doing.Length > 0 ? doing : CurrentState.Length > 0 ? CurrentState : "Idle";
    }

    /// <summary>Equal when it would be drawn the same (records compare lists by reference).</summary>
    public bool Same(AfcState? other) =>
        other is not null && JsonSerializer.Serialize(this) == JsonSerializer.Serialize(other);
}

/// <summary>
/// Armored Turtle's AFC (Box Turtle, NightOwl and other units), from the printer objects it adds to
/// Klipper: AFC itself, AFC_stepper or AFC_lane per lane, AFC_hub, AFC_buffer and AFC_extruder. Also
/// the G-code its lanes are loaded and unloaded with, which the Spoolman module uses too. Every call
/// throws if the printer can't be reached.
/// </summary>
public sealed class AfcApi(IMoonrakerApi printer)
{
    /// <summary>Switches the toolhead to this lane (unloading the one there first).</summary>
    public static string LoadScript(string lane) => $"CHANGE_TOOL LANE={lane}";
    /// <summary>Takes this lane, the one in the toolhead, back out of it.</summary>
    public static string UnloadScript(string lane) => $"TOOL_UNLOAD LANE={lane}";
    /// <summary>Pulls this lane out of the unit, so the spool can come off.</summary>
    public static string EjectScript(string lane) => $"LANE_UNLOAD LANE={lane}";
    /// <summary>Sets the lane's Spoolman spool (null: none); AFC takes its colour and material from it.</summary>
    public static string SpoolScript(string lane, int? spool) =>
        $"SET_SPOOL_ID LANE={lane} SPOOL_ID={(spool is int n ? n.ToString(CultureInfo.InvariantCulture) : "")}";
    /// <summary>Carries on after an AFC error has been fixed.</summary>
    public const string ResumeScript = "AFC_RESUME";

    // The objects to ask for, found once (AFC's shape changes only with Klipper's config).
    private Objects? _objects;

    private sealed record Objects(IReadOnlyList<string> Lanes, string? Bypass);

    // What's read of each object (AFC_stepper has much more).
    private const string LaneFields = "name,unit,hub,map,prep,load,loaded_to_hub,tool_loaded,status,spool_id,color,filament_name,material,weight,initial_weight";
    private const string AfcFields = "current_state,current_load,current_lane,next_lane,current_toolchange,number_of_toolchanges,"
                                     + "error_state,message,bypass_state,units,lanes,hubs,buffers,extruders";

    /// <summary>Whether Klipper has AFC.</summary>
    public async Task<bool> HasAfcAsync(CancellationToken ct) => (await ListAsync(ct)).Contains("AFC");

    private async Task<List<string>> ListAsync(CancellationToken ct)
    {
        using var list = await printer.GetJsonAsync("/printer/objects/list", ct, TimeSpan.FromSeconds(3));
        var objects = list.RootElement.Prop("result").Prop("objects");
        return objects.ValueKind == JsonValueKind.Array ? objects.EnumerateArray().Select(o => o.GetString() ?? "").ToList() : [];
    }

    /// <summary>
    /// AFC's state now. Throws <see cref="InvalidOperationException"/> if Klipper has no AFC. After a
    /// Klipper restart the objects are looked up again.
    /// </summary>
    public async Task<AfcState> GetStateAsync(CancellationToken ct)
    {
        for (int attempt = 0; ; attempt++)
        {
            bool listed = _objects == null;
            _objects ??= Find(await ListAsync(ct));
            var objects = _objects;
            // First AFC, for the names of the rest.
            using var afc = await printer.GetJsonAsync("/printer/objects/query?AFC=" + AfcFields, ct, TimeSpan.FromSeconds(3));
            var top = afc.RootElement.Prop("result").Prop("status").Prop("AFC");
            if (top.ValueKind != JsonValueKind.Object)
            {
                _objects = null;
                if (listed || attempt > 0)
                    throw new InvalidOperationException("No AFC");
                continue;
            }
            var query = new List<string>();
            foreach (string lane in Names(top, "lanes"))
            {
                string? object_ = objects.Lanes.FirstOrDefault(o => o.EndsWith(" " + lane, StringComparison.Ordinal));
                if (object_ != null)
                    query.Add($"{Uri.EscapeDataString(object_)}={LaneFields}");
            }
            query.AddRange(Names(top, "hubs").Select(h => Uri.EscapeDataString("AFC_hub " + h) + "=state"));
            query.AddRange(Names(top, "buffers").Select(b => Uri.EscapeDataString("AFC_buffer " + b) + "=state,enabled"));
            query.AddRange(Names(top, "extruders").Select(e => Uri.EscapeDataString("AFC_extruder " + e)
                                                              + "=tool_start,tool_start_status,tool_end,tool_end_status,lane_loaded"));
            if (objects.Bypass != null)
                query.Add(Uri.EscapeDataString(objects.Bypass) + "=filament_detected");
            using var rest = await printer.GetJsonAsync("/printer/objects/query?" + string.Join("&", query), ct, TimeSpan.FromSeconds(3));
            var status = rest.RootElement.Prop("result").Prop("status");
            // A lane AFC lists that wasn't there when looked up: Klipper's config changed.
            if (!listed && attempt == 0 && Names(top, "lanes").Any(l => !objects.Lanes.Any(o => o.EndsWith(" " + l, StringComparison.Ordinal))))
            {
                _objects = null;
                continue;
            }
            return Read(top, status, objects.Lanes, objects.Bypass);
        }
    }

    private static Objects Find(IReadOnlyList<string> names) => new(
        names.Where(n => n.StartsWith("AFC_stepper ", StringComparison.Ordinal) || n.StartsWith("AFC_lane ", StringComparison.Ordinal)).ToList(),
        names.FirstOrDefault(n => n is "filament_switch_sensor virtual_bypass" or "filament_switch_sensor bypass"));

    private static List<string> Names(JsonElement e, string name)
    {
        var list = e.Prop(name);
        return list.ValueKind == JsonValueKind.Array
            ? list.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String).Select(x => x.GetString()!).Where(s => s.Length > 0).ToList()
            : [];
    }

    private static string? Text(JsonElement e, string name) => e.Str(name) is { Length: > 0 } s ? s : null;

    private static bool On(JsonElement e, string name) => e.Prop(name).ValueKind == JsonValueKind.True;

    /// <summary>
    /// AFC's state from its object (afc) and the others' (status, by object name). laneObjects are the
    /// lanes' object names ("AFC_stepper lane1"); bypass the bypass sensor's, or null for none.
    /// </summary>
    internal static AfcState Read(JsonElement afc, JsonElement status, IReadOnlyList<string> laneObjects, string? bypass)
    {
        var lanes = new List<AfcLane>();
        foreach (string name in Names(afc, "lanes"))
        {
            string? object_ = laneObjects.FirstOrDefault(o => o.EndsWith(" " + name, StringComparison.Ordinal));
            var l = object_ == null ? default : status.Prop(object_);
            if (l.ValueKind != JsonValueKind.Object)
                continue;
            string color = l.Str("color").Trim();
            if (color.Length > 0 && !color.StartsWith('#'))
                color = "#" + color;
            lanes.Add(new AfcLane(name, l.Str("unit"), l.Str("hub"), l.Str("map"), On(l, "prep"), On(l, "load"),
                                  On(l, "loaded_to_hub"), On(l, "tool_loaded"), l.Str("status"),
                                  l.Opt("spool_id") is double id and > 0 ? (int)id : null, color,
                                  l.Str("filament_name").Trim(), l.Str("material").Trim(), l.Opt("weight"), l.Opt("initial_weight")));
        }
        var hubs = Names(afc, "hubs").Select(h => new AfcHub(h, On(status.Prop("AFC_hub " + h), "state"), h + "_Hub")).ToList();
        var buffers = Names(afc, "buffers").Select(b =>
        {
            var buffer = status.Prop("AFC_buffer " + b);
            return new AfcBuffer(b, buffer.Str("state"), On(buffer, "enabled"));
        }).ToList();
        var extruders = Names(afc, "extruders").Select(e =>
        {
            var x = status.Prop("AFC_extruder " + e);
            // A toolhead without a sensor there has none set (or uses the buffer as one).
            bool HasSensor(string pin) => x.Str(pin).Trim() is { Length: > 0 } p && p != "buffer";
            return new AfcExtruder(e, HasSensor("tool_start") ? On(x, "tool_start_status") : null,
                                   HasSensor("tool_end") ? On(x, "tool_end_status") : null,
                                   e + "_tool_start", e + "_tool_end", Text(x, "lane_loaded"));
        }).ToList();
        // "Night_Owl NightOwl": its type, then its name.
        var units = Names(afc, "units").Select(u => u.Contains(' ') ? u[(u.LastIndexOf(' ') + 1)..] : u).ToList();
        var message = afc.Prop("message");
        return new AfcState(units, lanes, hubs, buffers, extruders,
            afc.Str("current_state"), Text(afc, "current_load"), Text(afc, "current_lane"), Text(afc, "next_lane"),
            (int)afc.Num("current_toolchange"), (int)afc.Num("number_of_toolchanges"),
            On(afc, "error_state"),
            message.ValueKind == JsonValueKind.String ? message.GetString()! : message.Str("message"),
            message.Str("type"),
            On(afc, "bypass_state"), bypass != null);
    }
}

/// <summary>
/// Fetches AFC's state for the page and the LANE tile, which share it: whichever asks first fetches,
/// and the other gets the same answer if it's recent. Only asked while the popup shows.
/// </summary>
public sealed class AfcSource
{
    private static readonly ConditionalWeakTable<IMoonrakerApi, AfcSource> ForPrinter = new();

    public static AfcSource For(IMoonrakerApi printer) => ForPrinter.GetValue(printer, p => new AfcSource(new AfcApi(p)));

    public AfcApi Api { get; }
    /// <summary>The last state fetched, or null before the first.</summary>
    public AfcState? State { get; private set; }

    private readonly SemaphoreSlim _busy = new(1, 1);
    private DateTime _fetched = DateTime.MinValue;

    public AfcSource(AfcApi api) => Api = api;

    /// <summary>The state, fetched again unless it's newer than maxAge. Throws if the printer can't be reached.</summary>
    public async Task<AfcState> GetAsync(TimeSpan maxAge, CancellationToken ct)
    {
        await _busy.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (State != null && DateTime.UtcNow - _fetched < maxAge)
                return State;
            var state = await Api.GetStateAsync(ct).ConfigureAwait(false);
            (State, _fetched) = (state, DateTime.UtcNow);
            return state;
        }
        finally { _busy.Release(); }
    }
}
