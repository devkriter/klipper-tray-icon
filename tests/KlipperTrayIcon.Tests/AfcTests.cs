using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using KlipperTrayIcon.Modules.Afc;
using KlipperTrayIcon.Modules.Spoolman;
using KlipperTrayIcon.Printers;
using Xunit;

namespace KlipperTrayIcon.Tests;

/// <summary>The AFC module against a fake Moonraker answering as a Voron with a 2-lane NightOwl does.</summary>
public class AfcTests
{
    // The Voron's objects/list, less what AFC doesn't read (other macros, heaters, steppers).
    private const string VoronObjects = """
        ["toolhead","extruder","heater_bed","filament_switch_sensor th_pre","filament_switch_sensor th_post",
         "gcode_macro AFC_CALIBRATION","gcode_macro AFC_RESET","AFC","filament_switch_sensor extruder_tool_start",
         "filament_switch_sensor extruder_tool_end","AFC_extruder extruder","filament_switch_sensor TN_expanded",
         "filament_switch_sensor TN_compressed","AFC_buffer TN","AFC_NightOwl NightOwl","filament_switch_sensor lane1_prep",
         "filament_switch_sensor lane1_load","AFC_stepper lane1","tmc2209 AFC_stepper lane1","filament_switch_sensor lane2_prep",
         "filament_switch_sensor lane2_load","AFC_stepper lane2","tmc2209 AFC_stepper lane2",
         "filament_switch_sensor NightOwl_Hub_Hub","AFC_hub NightOwl_Hub","tmc2209 extruder",
         "filament_switch_sensor virtual_bypass","filament_switch_sensor quiet_mode"]
        """;

    // Its answers to objects/query, as they came (2026-11-09): lane1 (T0, Dark Teal ABS) and lane2
    // (T1, no spool) both at the hub, AFC loading lane2 after "Hub not clear".
    private const string VoronStatus = """
        {"AFC":{"current_load":null,"current_lane":"lane2","next_lane":"lane2","current_state":"Loading",
                "current_toolchange":0,"number_of_toolchanges":0,"spoolman":"http://localhost:7912","td1_present":false,
                "error_state":false,"bypass_state":false,"quiet_mode":false,"position_saved":true,
                "units":["Night_Owl NightOwl"],"lanes":["lane1","lane2"],"maps":["T0","T1"],"extruders":["extruder"],
                "hubs":["NightOwl_Hub"],"buffers":["TN"],
                "message":{"message":"lane2 Hub not clear when trying to load.\nPlease check that hub does not contain broken filament and is clear","type":"error"},
                "led_state":true},
         "AFC_stepper lane1":{"name":"lane1","unit":"NightOwl","hub":"NightOwl_Hub","extruder":"extruder","buffer":"TN",
                "buffer_status":"Advancing","lane":1,"map":"T0","load":true,"prep":true,"tool_loaded":false,"loaded_to_hub":true,
                "material":"ABS","spool_id":17,"color":"#00677F","filament_name":"Dark Teal","multi_color_hexes":[],
                "initial_weight":1000.0,"weight":936.468946109195,"extruder_temp":250,"bed_temp":110,"runout_lane":"lane2",
                "filament_status":"Ready","status":"Loaded","dist_hub":32.07},
         "AFC_stepper lane2":{"name":"lane2","unit":"NightOwl","hub":"NightOwl_Hub","extruder":"extruder","buffer":"TN",
                "buffer_status":"Advancing","lane":2,"map":"T1","load":true,"prep":true,"tool_loaded":false,"loaded_to_hub":true,
                "material":null,"spool_id":null,"color":"","filament_name":"","multi_color_hexes":[],"initial_weight":1000,
                "weight":1000,"extruder_temp":null,"bed_temp":null,"runout_lane":"lane1","filament_status":"Ready",
                "status":"Tool Loading","dist_hub":34.18},
         "AFC_hub NightOwl_Hub":{"state":false,"cut":false,"cut_cmd":"AFC","lanes":["lane1","lane2"],"afc_bowden_length":840.94},
         "AFC_buffer TN":{"state":"Advancing","lanes":["lane1","lane2"],"enabled":false,"active_lane":null},
         "AFC_extruder extruder":{"tool_stn":56.0,"buffer":null,"lane_loaded":null,"tool_start":"^thb:FS_PRE",
                "tool_start_status":false,"tool_end":"^thb:FS_POST","tool_end_status":false,"lanes":["lane1","lane2"],
                "status":"Idle"},
         "AFC_NightOwl NightOwl":{"lanes":["lane1","lane2"],"extruders":["extruder"],"hubs":["NightOwl_Hub"],"buffers":["TN"]},
         "filament_switch_sensor virtual_bypass":{"filament_detected":false,"enabled":false}}
        """;

    /// <summary>A fake Moonraker: objects/list, and objects/query answering with the objects asked for.</summary>
    private sealed class FakeMoonraker : HttpMessageHandler
    {
        public readonly List<string> Requests = new();
        public string Objects = VoronObjects;
        public JsonObject Status = JsonNode.Parse(VoronStatus)!.AsObject();

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            string path = Uri.UnescapeDataString(request.RequestUri!.PathAndQuery);
            lock (Requests)
                Requests.Add($"{request.Method} {path}");
            string? json = null;
            if (path == "/printer/objects/list")
                json = """{"result":{"objects":""" + Objects + "}}";
            else if (path.StartsWith("/printer/objects/query?"))
            {
                var asked = new JsonObject();
                foreach (string part in path["/printer/objects/query?".Length..].Split('&'))
                {
                    string name = part.Split('=')[0];
                    if (Status[name] is { } found)
                        asked[name] = found.DeepClone();
                }
                json = new JsonObject { ["result"] = new JsonObject { ["status"] = asked } }.ToJsonString();
            }
            return Task.FromResult(json == null
                ? new HttpResponseMessage(HttpStatusCode.NotFound)
                : new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") });
        }

        public void Set(string obj, string field, JsonNode? value) => Status[obj]![field] = value;
    }

    private static (AfcApi Api, FakeMoonraker Printer) Make()
    {
        var printer = new FakeMoonraker();
        return (new AfcApi(new MoonrakerBackend("p", http: new HttpClient(printer))), printer);
    }

    [Fact]
    public async Task ReadsTheNightOwlsLanesHubBufferAndToolhead()
    {
        var (api, _) = Make();
        var state = await api.GetStateAsync(default);
        Assert.Equal(["NightOwl"], state.Units);
        Assert.Equal("NIGHTOWL", state.UnitTitle);
        Assert.Equal(["lane1", "lane2"], state.Lanes.Select(l => l.Name));
        var teal = state.Lanes[0];
        Assert.Equal(("T0", "Dark Teal", "ABS", "#00677F", 17), (teal.Title, teal.Filament, teal.Material, teal.Color, teal.SpoolId));
        Assert.True(teal.Prep && teal.Load && teal.LoadedToHub && !teal.ToolLoaded && !teal.Empty);
        Assert.Equal(0.936, teal.Fraction!.Value, 3);
        var black = state.Lanes[1];
        Assert.Equal(("T1", "", "", null), (black.Title, black.Filament, black.Material, black.SpoolId));
        Assert.Null(black.Fraction); // AFC's 1000 g without a spool means nothing
        Assert.Equal([new AfcHub("NightOwl_Hub", false, "NightOwl_Hub_Hub")], state.Hubs);
        Assert.Equal([new AfcBuffer("TN", "Advancing", false)], state.Buffers);
        Assert.Equal([new AfcExtruder("extruder", false, false, "extruder_tool_start", "extruder_tool_end", null)], state.Extruders);
        Assert.True(state.HasBypass);
        Assert.False(state.Bypass);
        Assert.Null(state.Loaded);
    }

    [Fact]
    public async Task SaysWhatAfcIsDoingAndItsError()
    {
        var (api, printer) = Make();
        var state = await api.GetStateAsync(default);
        Assert.True(state.Busy);
        Assert.Equal("Loading T1", state.Describe(printing: false));
        Assert.False(state.Error);
        Assert.Equal("lane2 Hub not clear when trying to load.", state.Problem);
        Assert.Equal((Culprit.Hub, "lane2"), AfcLook.Blame(state));

        printer.Set("AFC", "error_state", true);
        printer.Set("AFC", "message", JsonNode.Parse("""{"message":"lane2 failed to load to hub","type":"error"}"""));
        state = await api.GetStateAsync(default);
        Assert.Equal("Error", state.Describe(printing: false));
        Assert.Equal((Culprit.Hub, "lane2"), AfcLook.Blame(state));
        Assert.Equal(new StatusText.Tile("—", "Error", Highlight: true), LaneTile.Describe(state, printing: false));

        printer.Set("AFC", "error_state", false);
        printer.Set("AFC", "message", JsonNode.Parse("""{"message":"","type":""}"""));
        state = await api.GetStateAsync(default);
        Assert.Equal("", state.Problem);
        Assert.Equal((Culprit.None, null), AfcLook.Blame(state));
    }

    [Fact]
    public async Task InAPrintsToolChangeItSaysWhichChange()
    {
        var (api, printer) = Make();
        printer.Set("AFC", "current_toolchange", 3);
        printer.Set("AFC", "number_of_toolchanges", 12);
        printer.Set("AFC", "message", JsonNode.Parse("""{"message":"","type":""}"""));
        var state = await api.GetStateAsync(default);
        Assert.Equal("Change 3 of 12 · Loading T1", state.Describe(printing: true));
        Assert.Equal(new StatusText.Tile("T1", "Change 3 of 12"), LaneTile.Describe(state, printing: true));

        // Done: T1 in the toolhead, printing.
        printer.Set("AFC", "current_state", "Idle");
        printer.Set("AFC", "current_load", "lane2");
        printer.Set("AFC_stepper lane2", "tool_loaded", true);
        printer.Set("AFC_extruder extruder", "tool_start_status", true);
        printer.Set("AFC_extruder extruder", "tool_end_status", true);
        state = await api.GetStateAsync(default);
        Assert.Equal("Printing T1", state.Describe(printing: true));
        Assert.Equal("Idle", state.Describe(printing: false));
        Assert.Equal("lane2", state.Loaded?.Name);
        Assert.Equal("lane2", AfcLook.PathLane(state)?.Name);
        Assert.True(AfcLook.PastHub(state, state.Loaded!));
        Assert.Equal(new StatusText.Tile("T1", ""), LaneTile.Describe(state, printing: true)); // no spool: nothing to name
    }

    [Fact]
    public async Task TheLaneTileNamesTheToolAndItsFilament()
    {
        var (api, printer) = Make();
        printer.Set("AFC", "current_state", "Idle");
        printer.Set("AFC", "message", JsonNode.Parse("""{"message":"","type":""}"""));
        Assert.Equal(new StatusText.Tile("—", "Unloaded"), LaneTile.Describe(await api.GetStateAsync(default), printing: false));
        printer.Set("AFC", "current_load", "lane1");
        Assert.Equal(new StatusText.Tile("T0", "Dark Teal ABS"), LaneTile.Describe(await api.GetStateAsync(default), printing: false));
        printer.Set("AFC", "bypass_state", true);
        Assert.Equal(new StatusText.Tile("—", "Bypass"), LaneTile.Describe(await api.GetStateAsync(default), printing: false));
        Assert.Equal(new StatusText.Tile("—", ""), LaneTile.Describe(null, printing: false));
    }

    [Fact]
    public async Task AnEmptyLaneSeesNoFilament()
    {
        var (api, printer) = Make();
        printer.Set("AFC_stepper lane2", "prep", false);
        printer.Set("AFC_stepper lane2", "load", false);
        printer.Set("AFC_stepper lane2", "loaded_to_hub", false);
        var state = await api.GetStateAsync(default);
        Assert.True(state.Lanes[1].Empty);
        Assert.False(state.Lanes[0].Empty);
    }

    [Fact]
    public async Task ObjectsAreListedOnceAndOnlyAfcsAreAskedFor()
    {
        var (api, printer) = Make();
        await api.GetStateAsync(default);
        await api.GetStateAsync(default);
        Assert.Single(printer.Requests, r => r == "GET /printer/objects/list");
        var queries = printer.Requests.Where(r => r.StartsWith("GET /printer/objects/query?")).ToList();
        Assert.Equal(4, queries.Count);
        Assert.All(queries, q => Assert.DoesNotContain("tmc2209", q));
        Assert.Contains(queries, q => q.Contains("AFC_stepper lane2=name,") && q.Contains("filament_switch_sensor virtual_bypass="));
    }

    [Fact]
    public async Task ANewLaneAfterAKlipperRestartIsLookedUpAgain()
    {
        var (api, printer) = Make();
        Assert.Equal(2, (await api.GetStateAsync(default)).Lanes.Count);
        // A third lane, as AFC_lane (newer AFC's name for it).
        printer.Objects = VoronObjects.Replace("\"AFC\",", "\"AFC\",\"AFC_lane lane3\",");
        printer.Set("AFC", "lanes", JsonNode.Parse("""["lane1","lane2","lane3"]"""));
        printer.Status["AFC_lane lane3"] = JsonNode.Parse("""
            {"name":"lane3","unit":"NightOwl","hub":"NightOwl_Hub","map":"T2","prep":true,"load":true,"color":"FF0000"}
            """);
        var state = await api.GetStateAsync(default);
        Assert.Equal(["T0", "T1", "T2"], state.Lanes.Select(l => l.Title));
        Assert.Equal("#FF0000", state.Lanes[2].Color);
        Assert.Equal(2, printer.Requests.Count(r => r == "GET /printer/objects/list"));
    }

    [Fact]
    public async Task APrinterWithoutAfcHasNone()
    {
        var (api, printer) = Make();
        printer.Objects = """["toolhead","extruder","heater_bed"]""";
        printer.Status = new JsonObject();
        Assert.False(await api.HasAfcAsync(default));
        await Assert.ThrowsAsync<InvalidOperationException>(() => api.GetStateAsync(default));
        Assert.Equal("No AFC in Klipper",
                     await new AfcModule().CheckAsync(new MoonrakerBackend("p", http: new HttpClient(printer)), default));
        Assert.True(await Make().Api.HasAfcAsync(default));
    }

    [Fact]
    public void LanesLoadEjectUnloadAndResumeWithAfcsCommands()
    {
        Assert.Equal("CHANGE_TOOL LANE=lane2", AfcApi.LoadScript("lane2"));
        Assert.Equal("TOOL_UNLOAD LANE=lane1", AfcApi.UnloadScript("lane1"));
        Assert.Equal("LANE_UNLOAD LANE=lane2", AfcApi.EjectScript("lane2"));
        Assert.Equal("SET_SPOOL_ID LANE=lane2 SPOOL_ID=16", AfcApi.SpoolScript("lane2", 16));
        Assert.Equal("SET_SPOOL_ID LANE=lane2 SPOOL_ID=", AfcApi.SpoolScript("lane2", null));
        Assert.Equal("AFC_RESUME", AfcApi.ResumeScript);
        // The Filament page's lane cards send the same.
        Assert.Equal(AfcApi.LoadScript("lane2"), new Tool("T1", 16, "lane2").LaneScript(load: true, inUse: false));
    }

    [Fact]
    public void ColoursAreReadAndDarkOnesGetAnEdge()
    {
        Assert.Equal(System.Windows.Media.Color.FromRgb(0x00, 0x67, 0x7F), AfcLook.ColorOf("#00677F"));
        Assert.Equal(System.Windows.Media.Color.FromRgb(0xFF, 0x00, 0x00), AfcLook.ColorOf("FF0000FF"));
        Assert.Null(AfcLook.ColorOf(""));
        Assert.Null(AfcLook.ColorOf("teal"));
        Assert.True(AfcLook.Dark(AfcLook.ColorOf("#0B0B0B")!.Value));
        Assert.False(AfcLook.Dark(AfcLook.ColorOf("#00677F")!.Value));
    }

    [Fact]
    public async Task TheSourceSharesARecentAnswer()
    {
        var (api, printer) = Make();
        var source = new AfcSource(api);
        var first = await source.GetAsync(TimeSpan.FromSeconds(5), default);
        Assert.Same(first, await source.GetAsync(TimeSpan.FromSeconds(5), default));
        Assert.NotSame(first, await source.GetAsync(TimeSpan.Zero, default));
        Assert.True(first.Same(source.State));
        printer.Set("AFC", "current_state", "Idle");
        Assert.False(first.Same(await source.GetAsync(TimeSpan.Zero, default)));
    }
}
