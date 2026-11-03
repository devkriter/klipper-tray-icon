using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using KlipperTrayIcon.Modules.Spoolman;
using KlipperTrayIcon.Printers;
using Xunit;

namespace KlipperTrayIcon.Tests;

/// <summary>The Spoolman module against Moonraker's [spoolman] API and its proxy to Spoolman.</summary>
public class SpoolmanTests
{
    private const string AsaSpool = """
        {"id":7,"registered":"2026-08-01T10:00:00","first_used":"2026-08-12T09:00:00","last_used":"2026-11-01T12:00:00",
         "filament":{"id":3,"name":"PolyLite ASA Red","vendor":{"id":1,"name":"Polymaker"},"material":"ASA","price":29.99,
                     "weight":1000,"color_hex":"C8312B","settings_extruder_temp":250,"settings_bed_temp":105},
         "remaining_weight":310,"initial_weight":1000,"remaining_length":104000,"location":"Dry box 1","lot_nr":"2406-118",
         "comment":"Dried 6 h","archived":false}
        """;
    private const string PlaSpool = """
        {"id":12,"last_used":"2026-10-31T12:00:00","filament":{"id":4,"name":"PLA Galaxy Black","vendor":{"id":2,"name":"Prusament"},
         "material":"PLA","weight":1000,"color_hex":"222222"},"remaining_weight":742,"remaining_length":248000,"location":"Shelf A"}
        """;
    private const string TriColorSpool = """
        {"id":3,"last_used":null,"filament":{"id":5,"name":"Silk Tri-Color PLA","material":"PLA","weight":1000,
         "multi_color_hexes":"C62828,D4A017,1E5BB8"},"remaining_weight":85,"initial_weight":1000}
        """;
    private const string ArchivedSpool = """
        {"id":1,"last_used":"2026-11-02T12:00:00","filament":{"id":6,"name":"Old","material":"PLA"},"remaining_weight":0,"archived":true}
        """;

    /// <summary>A fake Moonraker with Spoolman behind it, which records what it's asked ("METHOD path body").</summary>
    private sealed class FakeMoonraker : HttpMessageHandler
    {
        public readonly List<string> Requests = new();
        public bool SpoolmanUp = true;
        public string Objects = """["toolhead","extruder","heater_bed","gcode_macro PRINT_START"]""";
        public string Query = """{"toolhead":{"extruder":"extruder"}}""";

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            string body = request.Content == null ? "" : await request.Content.ReadAsStringAsync(ct);
            string path = Uri.UnescapeDataString(request.RequestUri!.PathAndQuery);
            lock (Requests)
                Requests.Add($"{request.Method} {path}{(body.Length > 0 ? " " + body : "")}");
            string? json = path switch
            {
                "/server/info" => """{"result":{"components":["file_manager","spoolman","history"]}}""",
                "/server/spoolman/status" => SpoolmanUp
                    ? """{"result":{"spoolman_connected":true,"pending_reports":[],"spool_id":7}}"""
                    : """{"result":{"spoolman_connected":false,"pending_reports":[{"spool_id":7,"filament_used":1.2},{"spool_id":7,"filament_used":3}],"spool_id":7}}""",
                "/server/config" => """{"result":{"config":{"spoolman":{"server":"http://spoolman.local:7912","sync_rate":5}}}}""",
                "/printer/objects/list" => """{"result":{"objects":""" + Objects + "}}",
                "/server/files/metadata?filename=parts/Panel.gcode" => """
                    {"result":{"filament_weight_total":120,"filament_type":"ASA","estimated_time":3600}}
                    """,
                "/server/spoolman/proxy" => Proxy(JsonDocument.Parse(body).RootElement),
                _ when path.StartsWith("/printer/objects/query") => """{"result":{"status":""" + Query + "}}",
                _ => request.Method == HttpMethod.Get ? null : """{"result":"ok"}""",
            };
            return json == null
                ? new HttpResponseMessage(HttpStatusCode.NotFound)
                : new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
        }

        private string Proxy(JsonElement request)
        {
            if (!SpoolmanUp)
                return """{"result":{"response":null,"error":{"status_code":503,"message":"Spoolman server not available"}}}""";
            string? response = (request.GetProperty("request_method").GetString(), request.GetProperty("path").GetString()) switch
            {
                ("GET", "/v1/spool") => $"[{ArchivedSpool},{TriColorSpool},{PlaSpool},{AsaSpool}]",
                ("GET", "/v1/spool/7") => AsaSpool,
                ("GET", "/v1/spool/12") => PlaSpool,
                ("GET", "/v1/spool/3") => TriColorSpool,
                ("GET", "/v1/setting/currency") => """{"value":"\"EUR\"","is_set":true,"type":"string"}""",
                _ => null,
            };
            return response != null
                ? """{"result":{"response":""" + response + ""","error":null}}"""
                : """{"result":{"response":null,"error":{"status_code":404,"message":"No spool with that ID was found."}}}""";
        }
    }

    private static (SpoolmanApi Api, FakeMoonraker Printer) Make()
    {
        var printer = new FakeMoonraker();
        return (new SpoolmanApi(new MoonrakerBackend("p", http: new HttpClient(printer))), printer);
    }

    /// <summary>A Moonraker with four toolheads (T0…T3 macros with spool_id), each on its own extruder, T2 in use.</summary>
    private static (SpoolmanApi Api, FakeMoonraker Printer) MakeFourTools()
    {
        var (api, printer) = Make();
        printer.Objects = """
            ["toolhead","extruder","extruder1","extruder2","extruder3","gcode_macro T0","gcode_macro T1","gcode_macro T2",
             "gcode_macro T3","gcode_macro PRINT_START"]
            """;
        printer.Query = """
            {"toolhead":{"extruder":"extruder2"},"gcode_macro T0":{"spool_id":12},"gcode_macro T1":{"spool_id":null},
             "gcode_macro T2":{"spool_id":7},"gcode_macro T3":{"spool_id":3}}
            """;
        return (api, printer);
    }

    private static PrinterStatus Printing(double progress, string file = "parts/Panel.gcode") => new("printing", progress, file);

    [Fact]
    public async Task ReadsMoonrakersStatus()
    {
        var (api, printer) = Make();
        Assert.True(await api.HasSpoolmanAsync(default));
        Assert.Equal(new SpoolmanStatus(true, 0, 7), await api.GetStatusAsync(default));
        printer.SpoolmanUp = false;
        Assert.Equal(new SpoolmanStatus(false, 2, 7), await api.GetStatusAsync(default));
        Assert.Equal("http://spoolman.local:7912", await api.GetSpoolmanUrlAsync(default));
    }

    [Fact]
    public async Task ListsSpoolsThroughTheProxyMostRecentlyUsedFirst()
    {
        var (api, printer) = Make();
        var spools = await api.GetSpoolsAsync(default);
        // Archived ones are left out; never used goes last.
        Assert.Equal([7, 12, 3], spools.Select(s => s.Id));
        Assert.Contains(printer.Requests, r => r.StartsWith("POST /server/spoolman/proxy ")
                                               && r.Contains("\"request_method\":\"GET\"") && r.Contains("\"path\":\"/v1/spool\"")
                                               && r.Contains("\"use_v2_response\":true"));

        var asa = spools[0];
        Assert.Equal("PolyLite ASA Red", asa.Name);
        Assert.Equal("Polymaker PolyLite ASA Red", asa.Title);
        Assert.Equal("ASA", asa.Material);
        Assert.Equal(["C8312B"], asa.Colors);
        Assert.Equal(0.31, asa.Fraction!.Value, 3);
        Assert.Equal(104000, asa.Length);
        Assert.Equal("Dry box 1", asa.Location);
        Assert.Equal(new DateTimeOffset(2026, 11, 1, 12, 0, 0, TimeSpan.Zero), asa.LastUsed);
        Assert.Equal((250.0, 105.0), (asa.ExtruderTemp, asa.BedTemp));
        Assert.Equal(29.99, asa.Price); // the filament's, when the spool has none
        Assert.Equal("2406-118", asa.Lot);

        Assert.Equal("Prusament PLA Galaxy Black", spools[1].Title);
        Assert.Equal(0.742, spools[1].Fraction!.Value, 3); // no initial_weight: the filament's weight
        Assert.Equal(["C62828", "D4A017", "1E5BB8"], spools[2].Colors);
        Assert.Null(spools[2].LastUsed);
        Assert.Equal("EUR", await api.GetCurrencyAsync(default));
    }

    [Fact]
    public async Task SpoolmanSayingNoThrows()
    {
        var (api, printer) = Make();
        Assert.Equal(404, (await Assert.ThrowsAsync<SpoolmanException>(() => api.GetSpoolAsync(99, default))).StatusCode);
        printer.SpoolmanUp = false;
        var e = await Assert.ThrowsAsync<SpoolmanException>(() => api.GetSpoolsAsync(default));
        Assert.Equal("Spoolman server not available", e.Message);
    }

    [Fact]
    public async Task SwitchesTheActiveSpool()
    {
        var (api, printer) = Make();
        await api.SetActiveSpoolAsync(12, default);
        Assert.Contains("POST /server/spoolman/spool_id {\"spool_id\":12}", printer.Requests);
    }

    [Fact]
    public async Task APrinterWithoutToolMacrosHasOneSpool()
    {
        var (api, _) = Make();
        Assert.Equal(ToolSpools.None, await api.GetToolsAsync(default));
        var state = await new SpoolmanSource(api).GetAsync("", TimeSpan.Zero, default);
        Assert.False(state.MultiTool);
        Assert.Equal(7, state.ActiveSpool!.Id);
        Assert.Equal([7], state.LoadedIds);
    }

    [Fact]
    public async Task ReadsEachToolheadsSpoolFromItsMacro()
    {
        var (api, printer) = MakeFourTools();
        var tools = await api.GetToolsAsync(default);
        Assert.Equal([new Tool("T0", 12), new Tool("T1", null), new Tool("T2", 7), new Tool("T3", 3)], tools.Tools);
        Assert.Equal(2, tools.Active); // on extruder2
        Assert.Contains(printer.Requests, r => r.StartsWith("GET /printer/objects/query?toolhead=extruder&gcode_macro T0=spool_id")
                                               && r.Contains("&gcode_macro T3=spool_id"));
        Assert.DoesNotContain(printer.Requests, r => r.Contains("PRINT_START=spool_id"));

        var state = await new SpoolmanSource(api).GetAsync("", TimeSpan.Zero, default);
        Assert.True(state.MultiTool);
        Assert.Equal(7, state.ActiveSpool!.Id);
        Assert.Equal([3, 7, 12], state.Spools.Keys.Order()); // every tool's spool
        Assert.Equal([12, 7, 3], state.LoadedIds);
    }

    [Fact]
    public void ToolMacrosWithoutASpoolAreLeftOutAndAFilamentChangerFollowsTheActiveSpool()
    {
        var macros = new List<(string, string, int)> { ("gcode_macro T0", "T0", 0), ("gcode_macro T1", "T1", 1), ("gcode_macro T2", "T2", 2) };
        using var doc = JsonDocument.Parse("""
            {"toolhead":{"extruder":"extruder"},"gcode_macro T0":{"spool_id":4},"gcode_macro T1":{"other":1},
             "gcode_macro T2":{"spool_id":9}}
            """);
        var tools = SpoolmanApi.ReadTools(doc.RootElement, macros, new HashSet<string> { "extruder" });
        Assert.Equal([new Tool("T0", 4), new Tool("T2", 9)], tools.Tools);
        // One extruder for every tool: which one is in use isn't known from it.
        Assert.Equal(-1, tools.Active);
    }

    [Fact]
    public async Task OnAFilamentChangerTheToolInUseHoldsTheActiveSpool()
    {
        var (api, printer) = MakeFourTools();
        printer.Objects = """["toolhead","extruder","gcode_macro T0","gcode_macro T1","gcode_macro T2","gcode_macro T3"]""";
        var state = await new SpoolmanSource(api).GetAsync("", TimeSpan.Zero, default);
        Assert.Equal(2, state.Tools.Active); // T2 holds spool 7, Moonraker's active one
    }

    [Fact]
    public async Task PuttingASpoolInAToolSetsItsMacroAndTheActiveSpoolForTheToolInUse()
    {
        var (api, printer) = MakeFourTools();
        await api.SetToolSpoolAsync("T1", 3, active: false, default);
        Assert.Contains("POST /printer/gcode/script?script=SET_GCODE_VARIABLE MACRO=T1 VARIABLE=spool_id VALUE=3", printer.Requests);
        Assert.DoesNotContain(printer.Requests, r => r.StartsWith("POST /server/spoolman/spool_id"));
        await api.SetToolSpoolAsync("T2", 12, active: true, default);
        Assert.Contains("POST /printer/gcode/script?script=SET_GCODE_VARIABLE MACRO=T2 VARIABLE=spool_id VALUE=12", printer.Requests);
        Assert.Contains("POST /server/spoolman/spool_id {\"spool_id\":12}", printer.Requests);
    }

    [Fact]
    public async Task WhenSpoolmanIsDownTheLastSpoolsStayMarkedStale()
    {
        var (api, printer) = Make();
        var source = new SpoolmanSource(api);
        var fresh = await source.GetAsync("", TimeSpan.Zero, default);
        Assert.False(fresh.Stale);
        printer.SpoolmanUp = false;
        var stale = await source.GetAsync("", TimeSpan.Zero, default);
        Assert.True(stale.Stale);
        Assert.Equal(2, stale.Status.PendingReports);
        Assert.Equal(fresh.ActiveSpool, stale.ActiveSpool);
        // Recent enough: not asked again.
        int asked = printer.Requests.Count;
        await source.GetAsync("", TimeSpan.FromMinutes(1), default);
        Assert.Equal(asked, printer.Requests.Count);
    }

    [Fact]
    public void ReadsTheFilesFilament()
    {
        using var single = JsonDocument.Parse("""{"filament_weight_total":120.5,"filament_type":"ASA"}""");
        var one = SpoolmanApi.ReadFileFilament(single.RootElement);
        Assert.Equal(120.5, one.TotalWeight);
        Assert.Empty(one.Weights);
        Assert.Equal(["ASA"], one.Types);
        using var multi = JsonDocument.Parse("""{"filament_weight_total":50,"filament_weights":[10,0,40],"filament_type":"PLA;PLA;PETG"}""");
        var file = SpoolmanApi.ReadFileFilament(multi.RootElement);
        Assert.Equal([10.0, 0, 40], file.Weights);
        Assert.Equal(["PLA", "PLA", "PETG"], file.Types);
        Assert.Equal(FileFilament.None.Types, SpoolmanApi.ReadFileFilament(default).Types);
        // OrcaSlicer's list, as newer Moonraker passes it on.
        using var orca = JsonDocument.Parse("""{"filament_weights":[0.0,0.0,17.5],"filament_type":"[\"ASA\", \"ASA\", \"ABS\"]"}""");
        Assert.Equal(["ASA", "ASA", "ABS"], SpoolmanApi.ReadFileFilament(orca.RootElement).Types);
    }

    private static Spool Spool(int id, string material, double? left, double? start = 1000) =>
        new(id, $"Spool {id}", "", material, ["C8312B"], left, start, null);

    private static SpoolmanState State(FileFilament file, params Spool[] spools) =>
        new(new SpoolmanStatus(true, 0, spools.FirstOrDefault()?.Id), ToolSpools.None, spools.ToDictionary(s => s.Id), file);

    [Fact]
    public void ChecksTheSpoolAgainstThePrint()
    {
        var asa = Spool(7, "ASA", 310);
        var file = new FileFilament(120, [], ["ASA"]);
        var state = State(file, asa);
        // Half printed: 60 g to go, plenty left.
        var check = Spools.Check(asa, state, -1, Printing(0.5));
        Assert.Equal(60, check.Needed);
        Assert.Null(check.WrongMaterial);
        Assert.Null(Spools.Short(asa, check));
        // Not printing: nothing to check.
        Assert.Equal(SpoolCheck.None, Spools.Check(asa, state, -1, new PrinterStatus("standby", 0)));

        var low = Spool(3, "PLA", 25);
        check = Spools.Check(low, State(file, low), -1, Printing(0));
        Assert.Equal(95, Spools.Short(low, check));
        Assert.Equal("ASA", check.WrongMaterial);

        // Loosely the same material.
        Assert.True(Spools.SameMaterial("PLA+", "PLA"));
        Assert.True(Spools.SameMaterial("petg", "PETG"));
        Assert.False(Spools.SameMaterial("ASA", "PLA"));
        Assert.Null(Spools.Check(Spool(1, "PLA", 500), State(new FileFilament(10, [], ["PLA+"]), Spool(1, "PLA", 500)), -1,
                                 Printing(0)).WrongMaterial);

        // One tool, a file with three filament slots of which only the third is printed: only it counts.
        var abs = Spool(2, "ABS", 108);
        var slots = new FileFilament(17.5, [0, 0, 17.5], ["ASA", "ASA", "ABS"]);
        Assert.Null(Spools.Check(abs, State(slots, abs), -1, Printing(0)).WrongMaterial);
        Assert.Equal("ABS", Spools.Check(asa, State(slots, asa), -1, Printing(0)).WrongMaterial);
        // No weights per slot: any of its materials will do.
        Assert.Null(Spools.Check(abs, State(slots with { Weights = [] }, abs), -1, Printing(0)).WrongMaterial);
        Assert.Equal("ASA or ABS", Spools.Check(low, State(slots with { Weights = [] }, low), -1, Printing(0)).WrongMaterial);
    }

    [Fact]
    public void ChecksEachToolheadAgainstItsShareOfThePrint()
    {
        Spool t0 = Spool(12, "PLA", 742), t1 = Spool(4, "PLA", 5), t2 = Spool(7, "ASA", 310);
        var tools = new ToolSpools([new Tool("T0", 12), new Tool("T1", 4), new Tool("T2", 7)], 2);
        var perTool = new SpoolmanState(new SpoolmanStatus(true, 0, 7), tools, new[] { t0, t1, t2 }.ToDictionary(s => s.Id),
                                        new FileFilament(60, [10, 20, 30], ["PLA", "PLA", "PETG"]));
        var status = Printing(0.5);
        Assert.Equal(5, Spools.Check(t0, perTool, 0, status).Needed);
        Assert.Equal(5, Spools.Short(t1, Spools.Check(t1, perTool, 1, status)));
        Assert.Equal(15, Spools.Check(t2, perTool, 2, status).Needed);
        Assert.Equal("PETG", Spools.Check(t2, perTool, 2, status).WrongMaterial);
        Assert.Null(Spools.Check(t0, perTool, 0, status).WrongMaterial);

        // Without weights per tool, the whole print is for the tool in use.
        var totalOnly = perTool with { File = new FileFilament(60, [], ["ASA"]) };
        Assert.Equal(30, Spools.Check(t2, totalOnly, 2, status).Needed);
        Assert.Equal(SpoolCheck.None, Spools.Check(t0, totalOnly, 0, status));

        // The tile shows the tool in use.
        Assert.Equal(new StatusText.Tile("310 g", "sliced for PETG", Highlight: true),
                     Spools.Tile(perTool, status, Levels.Default));
        Assert.Equal(new StatusText.Tile("310 g", "ASA · 31%"), Spools.Tile(totalOnly, status, Levels.Default));
    }

    [Fact]
    public void TheSpoolTile()
    {
        var idle = new PrinterStatus("standby", 0);
        Assert.Equal(new StatusText.Tile("—", ""), Spools.Tile(null, idle, Levels.Default));
        var none = new SpoolmanState(new SpoolmanStatus(true, 0, null), ToolSpools.None, new Dictionary<int, Spool>(), FileFilament.None);
        Assert.Equal(new StatusText.Tile("—", "none picked"), Spools.Tile(none, idle, Levels.Default));

        var asa = Spool(7, "ASA", 310);
        Assert.Equal(new StatusText.Tile("310 g", "ASA · 31%"), Spools.Tile(State(FileFilament.None, asa), idle, Levels.Default));
        // Not enough left for the rest of the print: amber, and by how much.
        var shortSpool = Spool(3, "ASA", 85);
        Assert.Equal(new StatusText.Tile("85 g", "35 g short", Highlight: true),
                     Spools.Tile(State(new FileFilament(120, [], ["ASA"]), shortSpool), Printing(0), Levels.Default));
        // Low, but enough: amber too.
        Assert.Equal(new StatusText.Tile("85 g", "ASA · 9%", Highlight: true),
                     Spools.Tile(State(new FileFilament(120, [], ["ASA"]), shortSpool), Printing(0.9), Levels.Default));
        // No starting weight: no percentage.
        Assert.Equal(new StatusText.Tile("500 g", "PLA"), Spools.Tile(State(FileFilament.None, Spool(1, "PLA", 500, null)), idle, Levels.Default));
        // Low as set in the Modules window.
        Assert.False(Spools.Tile(State(FileFilament.None, asa), idle, Levels.Default).Highlight);
        Assert.True(Spools.Tile(State(FileFilament.None, asa), idle, new Levels(400, 50)).Highlight);
    }

    [Fact]
    public void SaysWhenASpoolWasLastUsed()
    {
        var now = new DateTimeOffset(2026, 11, 2, 15, 0, 0, TimeSpan.Zero);
        Assert.Equal("not used yet", Spools.Used(null, now));
        Assert.Equal("used today", Spools.Used(now.AddHours(-1), now));
        Assert.Equal("used yesterday", Spools.Used(now.AddDays(-1), now));
        Assert.Equal("used 5 days ago", Spools.Used(now.AddDays(-5), now));
        Assert.Equal("used 2 weeks ago", Spools.Used(now.AddDays(-15), now));
        Assert.Equal("used 3 months ago", Spools.Used(now.AddDays(-95), now));
    }

    [Fact]
    public void FiltersByNameMaterialPlaceOrId()
    {
        var spool = new Spool(12, "PLA Galaxy Black", "Prusament", "PLA", [], 742, 1000, null, "Shelf A");
        Assert.True(SpoolmanPanel.Matches(spool, "galaxy"));
        Assert.True(SpoolmanPanel.Matches(spool, "prusament pla"));
        Assert.True(SpoolmanPanel.Matches(spool, "shelf a"));
        Assert.True(SpoolmanPanel.Matches(spool, "#12"));
        Assert.False(SpoolmanPanel.Matches(spool, "petg"));
    }
}
