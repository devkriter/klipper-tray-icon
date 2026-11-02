using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using KlipperTrayIcon;
using KlipperTrayIcon.Printers;
using KlipperTrayIcon.UI;
using Xunit;

namespace KlipperTrayIcon.Tests;

public class StatusTextTests
{
    [Fact]
    public void PrintingShowsProgressTimeLeftAndFile()
    {
        var now = new DateTime(2026, 1, 1, 12, 0, 0);
        var lines = StatusText.Lines(new PrinterStatus("printing", 0.456, "Benchy.gcode", 3900, "Slicer"), now);
        Assert.Equal(["Printing \u00b7 45%", "1h 5m left \u00b7 done 13:05 (slicer)", "Benchy.gcode"], lines);
    }

    [Fact]
    public void OfflineShowsReason() =>
        Assert.Equal(["Offline", "Can't reach http://x"], StatusText.Lines(PrinterStatus.Offline("Can't reach http://x")));

    [Fact]
    public void CompleteShowsFile() =>
        Assert.Equal(["Print complete", "a.gcode"], StatusText.Lines(new PrinterStatus("complete", 1, "a.gcode")));

    [Fact]
    public void StatsTilesWhilePrinting()
    {
        var now = new DateTime(2026, 1, 1, 23, 0, 0);
        var details = new PrintDetails(new Heater(214.6, 215), new Heater(48, 60), 57, 210, 11.4);
        var tiles = StatusText.StatsTiles(new PrinterStatus("printing", 0.42, "a.gcode", 5000, "Slicer", Details: details), now);
        Assert.Equal(new StatusText.Tile("215°", "of 215°"), tiles[0]);
        Assert.Equal(new StatusText.Tile("48°", "of 60°", Highlight: true), tiles[1]); // still heating
        Assert.Equal(new StatusText.Tile("42%", "Printing"), tiles[2]);
        Assert.Equal(new StatusText.Tile("57", "of 210"), tiles[3]);
        Assert.Equal(new StatusText.Tile("1h 23m", "Fri 00:23"), tiles[4]); // tomorrow: no room for "done"
    }

    [Fact]
    public void ElapsedTile()
    {
        var now = new DateTime(2026, 1, 2, 1, 30, 0);
        string[] tiles = [StatusText.Elapsed];
        PrinterStatus Status(string state, double? elapsed) =>
            new(state, 0.5, Details: new PrintDetails(null, null, Elapsed: elapsed));
        Assert.Equal(new StatusText.Tile("1h 0m", "since 00:30"), StatusText.StatsTiles(Status("printing", 3600), now, tiles)[0]);
        Assert.Equal(new StatusText.Tile("2h 0m", "since Thu 23:30"), StatusText.StatsTiles(Status("paused", 7200), now, tiles)[0]);
        Assert.Equal(new StatusText.Tile("2h 5m", "in total"), StatusText.StatsTiles(Status("complete", 7500), now, tiles)[0]);
        Assert.Equal(new StatusText.Tile("—", ""), StatusText.StatsTiles(Status("standby", null), now, tiles)[0]);
        Assert.Equal(["ELAPSED"], StatusText.StatsLabels(Status("printing", 1), tiles));
    }

    [Fact]
    public void ChamberTileOnlyWithAChamberSensor()
    {
        var details = new PrintDetails(new Heater(215, 215), new Heater(60, 60));
        var without = new PrinterStatus("printing", 0.42, Details: details with { Tiles = StatusText.DefaultTiles("") });
        Assert.Equal(["NOZZLE", "BED", "PROGRESS", "LAYER", "TIME LEFT"], StatusText.StatsLabels(without));
        Assert.Equal(5, StatusText.StatsTiles(without).Length);

        // A fan-controlled chamber shows its target like the bed.
        const string fan = "temperature_fan chamber";
        var with = new PrinterStatus("printing", 0.42, Details: details with
        {
            Tiles = StatusText.DefaultTiles(fan),
            Sensors = new Dictionary<string, Heater> { [fan] = new(50.2, 51) },
        });
        Assert.Equal(["NOZZLE", "BED", "CHAMBER", "PROGRESS", "LAYER", "TIME LEFT"], StatusText.StatsLabels(with));
        Assert.Equal(new StatusText.Tile("50°", "of 51°"), StatusText.StatsTiles(with)[2]);
        Assert.Equal("42%", StatusText.StatsTiles(with)[3].Value);
    }

    [Fact]
    public void NoLayerWhilePreparing()
    {
        // The start macro moves Z about: working a layer out from it would be nonsense.
        var status = new PrinterStatus("printing", 0, Details: new PrintDetails(null, null, Z: 25), Preparing: true);
        Assert.Equal(new StatusText.Tile("—", ""), StatusText.StatsTiles(status)[3]);
        Assert.Equal(new StatusText.Tile("0%", "Preparing"), StatusText.StatsTiles(status)[2]);
    }

    [Fact]
    public void ChosenTilesInTheirOrder()
    {
        const string heater = "heater_generic chamber", mcu = "temperature_sensor MCU", coil = "temperature_sensor beacon_coil";
        var status = new PrinterStatus("printing", 0.5, Details: new PrintDetails(new Heater(215, 215), null, Tool: "T1",
            Sensors: new Dictionary<string, Heater> { [heater] = new(38, 50), [mcu] = new(41.4, 0, Heats: false) }));
        string[] tiles = [StatusText.Nozzle, heater, mcu, coil, StatusText.Progress];
        Assert.Equal(["NOZZLE T1", "CHAMBER", "MCU", "BEACON COIL", "PROGRESS"], StatusText.StatsLabels(status, tiles));
        Assert.Equal([
            new StatusText.Tile("215°", "of 215°"),
            new StatusText.Tile("38°", "of 50°", Highlight: true), // a heated chamber reads like the bed
            new StatusText.Tile("41°", ""),
            new StatusText.Tile("—", ""), // chosen, but not reported
            new StatusText.Tile("50%", "Printing"),
        ], StatusText.StatsTiles(status, tiles: tiles));
        Assert.Empty(StatusText.StatsTiles(status, tiles: []));
    }

    [Theory]
    [InlineData("extruder,heater_bed,temperature_fan chamber,temperature_sensor MCU", "temperature_fan chamber")]
    [InlineData("temperature_sensor chamber,heater_generic chamber", "heater_generic chamber")]
    [InlineData("temperature_sensor chamber_top,temperature_sensor Chamber", "temperature_sensor Chamber")]
    [InlineData("temperature_sensor chamber_exhaust", "temperature_sensor chamber_exhaust")]
    [InlineData("extruder,heater_bed,temperature_sensor MCU", "")]
    public void FindsTheChamberSensor(string sensors, string expected) =>
        Assert.Equal(expected, MoonrakerBackend.FindChamber(sensors.Split(',')));

    [Fact]
    public void StatsTilesFallBackToZAndBlanks()
    {
        var printing = new PrinterStatus("printing", 0.1, Details: new PrintDetails(null, null, Z: 2.35));
        Assert.Equal(new StatusText.Tile("2.4", "mm (Z)"), StatusText.StatsTiles(printing)[3]);

        var idle = StatusText.StatsTiles(new PrinterStatus("standby", 0, Details: new PrintDetails(new Heater(23, 0), null)));
        Assert.Equal(new StatusText.Tile("23°", "off"), idle[0]);
        Assert.Equal(new StatusText.Tile("—", ""), idle[1]);
        Assert.Equal(new StatusText.Tile("—", "Idle"), idle[2]);
        Assert.Equal(new StatusText.Tile("—", ""), idle[3]);
        Assert.Equal(new StatusText.Tile("—", ""), idle[4]);
    }

    [Theory]
    [InlineData(1040, 480, false)]  // default popup
    [InlineData(1920, 1040, false)] // maximized on a 1080p screen
    [InlineData(1280, 1392, true)]  // snapped to half of a 4K screen
    [InlineData(500, 900, true)]    // tall and thin
    public void ConsoleGoesBelowTheVideoInTallWindows(double width, double height, bool stacked)
    {
        var (isStacked, videoHeight) = WebcamPopup.ChooseArrangement(width, height, 16.0 / 9);
        Assert.Equal(stacked, isStacked);
        if (stacked)
            Assert.Equal(Math.Min(width * 9 / 16, height - 220), videoHeight, 3);
    }

    [Fact]
    public void ShortenKeepsTheEnd() => Assert.Equal("\u2026def", StatusText.Shorten("abcdef", 4));

    [Theory]
    [InlineData("B:109.3 /110.0 T0:230.6 /250.0", true)]
    [InlineData("T:215.0 /215.0", true)]
    [InlineData("ok B:60.0 /60.0 T0:200.1 /210.0", true)]
    [InlineData("B:25.1 /0.0 T0:24.9 /0.0 C:30.0 /0.0", true)]
    [InlineData("// Klipper state: Ready", false)]
    [InlineData("M109 S250", false)]
    [InlineData("echo: B:1 /2 hello", false)]
    public void TemperatureReports(string line, bool expected) =>
        Assert.Equal(expected, StatusText.TemperatureReport().IsMatch(line));
}

public class ConfigTests
{
    [Fact]
    public void ReadsPythonEraConfigAndKeepsUnknownKeys()
    {
        const string json = """
            { "printer_type": "klipper", "moonraker_url": "http://voron.local",
              "update_interval_seconds": 2.0, "notifications": false, "some_future_key": [1, 2] }
            """;
        var config = JsonSerializer.Deserialize<AppConfig>(json, ConfigStore.JsonOptions)!;
        Assert.Equal("http://voron.local", config.MoonrakerUrl);
        Assert.False(config.Notifications);
        Assert.True(config.WebcamConsole); // default for a key the old file didn't have
        Assert.Equal(TimeSpan.FromSeconds(2), config.UpdateInterval);
        string saved = JsonSerializer.Serialize(config, ConfigStore.JsonOptions);
        Assert.Contains("some_future_key", saved);
        Assert.Contains("\"moonraker_url\"", saved);
    }
}

public class MoonrakerTests
{
    [Theory]
    [InlineData(" mainsail.local/ ", "http://mainsail.local")]
    [InlineData("https://printer:7125", "https://printer:7125")]
    [InlineData("", "")]
    public void NormalizesUrls(string input, string expected) =>
        Assert.Equal(expected, MoonrakerBackend.NormalizeUrl(input));

    [Fact]
    public void SendsApiKeyOnlyWhenSet()
    {
        Assert.Equal("k", new MoonrakerBackend("x", " k ").Headers["X-Api-Key"]);
        Assert.Empty(new MoonrakerBackend("x").Headers);
    }

    [Fact]
    public async Task ParsesStatusAndUsesSlicerEstimate()
    {
        var http = Stub(path => path switch
        {
            var p when p.StartsWith("/printer/objects/query") => """
                {"result":{"status":{"print_stats":{"state":"printing","filename":"Benchy.gcode","print_duration":600},
                 "virtual_sdcard":{"progress":0.25},"display_status":{"progress":0.2}}}}
                """,
            var p when p.StartsWith("/server/files/metadata") => """{"result":{"estimated_time":2400}}""",
            _ => null,
        });
        var status = await new MoonrakerBackend("printer.local", http: http).GetStatusAsync(default);
        Assert.Equal("printing", status.State);
        Assert.Equal(0.25, status.Progress);
        Assert.Equal(1800, status.TimeLeftSeconds);
        Assert.Equal("Slicer", status.EtaSource);
        Assert.False(status.Preparing);
    }

    [Fact]
    public async Task PreparingUntilTheFirstExtrusion()
    {
        // Start macro and heat soak: Klipper says printing, but its print clock hasn't started.
        var http = Stub(path => path switch
        {
            var p when p.StartsWith("/printer/objects/query") => """
                {"result":{"status":{"print_stats":{"state":"printing","filename":"Benchy.gcode","print_duration":0,
                 "total_duration":240},"virtual_sdcard":{"progress":0.001}}}}
                """,
            var p when p.StartsWith("/server/files/metadata") => """{"result":{"estimated_time":2400}}""",
            _ => null,
        });
        var status = await new MoonrakerBackend("p", http: http).GetStatusAsync(default);
        Assert.Equal("printing", status.State);
        Assert.True(status.Preparing);
        Assert.Equal("Preparing to print", StatusText.Lines(status)[0]);
        Assert.Equal(240, status.Details?.Elapsed); // elapsed counts the preparing too
    }

    [Fact]
    public async Task FindsAndReadsTheChamberByDefault()
    {
        var queries = new List<string>();
        var http = Stub(path =>
        {
            if (!path.StartsWith("/printer/objects/query"))
                return path.StartsWith("/server/files/metadata") ? """{"result":{}}""" : null;
            queries.Add(Uri.UnescapeDataString(path));
            return """
                {"result":{"status":{"print_stats":{"state":"standby"},
                 "heaters":{"available_sensors":["extruder","heater_bed","temperature_fan chamber"]},
                 "temperature_fan chamber":{"temperature":46.02,"target":51.0}}}}
                """;
        });
        var backend = new MoonrakerBackend("p", http: http);
        await backend.GetStatusAsync(default); // finds it
        var second = (await backend.GetStatusAsync(default)).Details!; // reads it
        Assert.Contains("&heaters=available_sensors", queries[0]);
        Assert.Contains("&temperature_fan chamber=temperature,target", queries[1]);
        Assert.DoesNotContain("heaters=", queries[1]);
        Assert.Equal(new Heater(46.02, 51), second.Sensors!["temperature_fan chamber"]);
        Assert.Equal(StatusText.DefaultTiles("temperature_fan chamber"), second.Tiles);
    }

    [Fact]
    public async Task ChosenTilesAskForTheirSensorsOnly()
    {
        var queries = new List<string>();
        var http = Stub(path =>
        {
            if (!path.StartsWith("/printer/objects/query"))
                return null;
            queries.Add(Uri.UnescapeDataString(path));
            return """{"result":{"status":{"print_stats":{"state":"standby"},"temperature_sensor MCU":{"temperature":41.4}}}}""";
        });
        string[] tiles = ["temperature_sensor MCU", "progress"];
        var details = (await new MoonrakerBackend("p", http: http, statsTiles: tiles).GetStatusAsync(default)).Details!;
        Assert.DoesNotContain("heaters=", queries[0]); // no need to look for a chamber
        Assert.Contains("&temperature_sensor MCU=temperature,target", queries[0]);
        Assert.Equal(tiles, details.Tiles);
        Assert.Equal(41.4, details.Sensors!["temperature_sensor MCU"].Temperature);
    }

    [Fact]
    public async Task ListsSensorsForSettings()
    {
        var http = Stub(path => path.StartsWith("/printer/objects/query?heaters") ? """
            {"result":{"status":{"heaters":{"available_sensors":
              ["temperature_sensor beacon_coil","heater_bed","temperature_fan chamber","extruder","extruder1"]}}}}
            """ : null);
        Assert.Equal(["temperature_sensor beacon_coil", "temperature_fan chamber"],
                     await new MoonrakerBackend("p", http: http).GetSensorsAsync(default));
    }

    [Fact]
    public async Task ReadsTemperaturesAndLayers()
    {
        var http = Stub(path => path switch
        {
            var p when p.StartsWith("/printer/objects/query") => """
                {"result":{"status":{"print_stats":{"state":"printing","filename":"a.gcode","print_duration":60,
                   "info":{"current_layer":12,"total_layer":80}},
                 "extruder":{"temperature":214.8,"target":215},"heater_bed":{"temperature":60.1,"target":60},
                 "gcode_move":{"gcode_position":[1,2,2.6,0]}}}}
                """,
            var p when p.StartsWith("/server/files/metadata") => """{"result":{}}""",
            _ => null,
        });
        var details = (await new MoonrakerBackend("p", http: http).GetStatusAsync(default)).Details!;
        Assert.Equal((new Heater(214.8, 215), new Heater(60.1, 60), 12, 80, 2.6),
                     (details.Nozzle, details.Bed, details.Layer, details.TotalLayers, details.Z));
    }

    [Fact]
    public async Task NozzleFollowsTheActiveToolOnToolchangers()
    {
        // A toolchanger (e.g. Snapmaker U1) printing with T2: the first poll only learns
        // which extruder is in use; from the next one the nozzle tile shows that one.
        var queries = new List<string>();
        var http = Stub(path =>
        {
            if (!path.StartsWith("/printer/objects/query"))
                return path.StartsWith("/server/files/metadata") ? """{"result":{}}""" : null;
            queries.Add(path);
            return """
                {"result":{"status":{"print_stats":{"state":"printing","filename":"a.gcode"},
                 "toolhead":{"extruder":"extruder2"},
                 "extruder":{"temperature":25,"target":0},"extruder2":{"temperature":219.6,"target":220}}}}
                """;
        });
        var backend = new MoonrakerBackend("p", http: http);
        var first = (await backend.GetStatusAsync(default)).Details!;
        var second = (await backend.GetStatusAsync(default)).Details!;
        Assert.Equal((new Heater(25, 0), "T0"), (first.Nozzle, first.Tool));
        Assert.Equal((new Heater(219.6, 220), "T2"), (second.Nozzle, second.Tool));
        Assert.Contains("&extruder2=temperature,target", queries[1]);
    }

    [Fact]
    public async Task WorksOutLayersFromZWhenKlipperDoesNotKnow()
    {
        // No SET_PRINT_STATS_INFO from the slicer: info fields are null.
        var http = Stub(path => path switch
        {
            var p when p.StartsWith("/printer/objects/query") => """
                {"result":{"status":{"print_stats":{"state":"printing","filename":"a.gcode",
                   "info":{"current_layer":null,"total_layer":null}},
                 "extruder":{"temperature":215,"target":215},"gcode_move":{"gcode_position":[0,0,1.0,0]}}}}
                """,
            var p when p.StartsWith("/server/files/metadata") =>
                """{"result":{"layer_height":0.2,"first_layer_height":0.2,"object_height":20}}""",
            _ => null,
        });
        var details = (await new MoonrakerBackend("p", http: http).GetStatusAsync(default)).Details!;
        Assert.Equal(5, details.Layer);
        Assert.Equal(100, details.TotalLayers);
        Assert.Null(details.Bed);
    }

    [Fact]
    public async Task ReadsMainsailConsoleSettings()
    {
        var http = Stub(path => path.StartsWith("/server/database/item?namespace=mainsail&key=console")
            ? """{"result":{"namespace":"mainsail","key":"console","value":{"hideTlCommands":false}}}"""
            : null);
        var filter = await new MoonrakerBackend("p", http: http).GetConsoleFilterAsync(default);
        Assert.Equal([ConsoleFilter.WaitTemperatures], filter.Rules);
        // No Mainsail settings on the printer (404): Mainsail's defaults.
        var none = await new MoonrakerBackend("p", http: Stub(_ => null)).GetConsoleFilterAsync(default);
        Assert.True(none.SameAs(ConsoleFilter.MainsailDefaults));
    }

    [Fact]
    public async Task ExplainsAuthFailures()
    {
        var http = Stub(_ => null, HttpStatusCode.Unauthorized);
        var noKey = await new MoonrakerBackend("p", http: http).TestConnectionAsync(default);
        var badKey = await new MoonrakerBackend("p", "wrong", http).TestConnectionAsync(default);
        Assert.Equal((false, "Moonraker refused access - enter an API key in Settings"), noKey);
        Assert.Equal((false, "Moonraker rejected the API key"), badKey);
    }

    [Fact]
    public async Task OfflineWhenUnreachable()
    {
        var status = await new MoonrakerBackend("http://127.0.0.1:9").GetStatusAsync(default);
        Assert.Equal("offline", status.State);
        // Refused connections on Windows retry for ~2s, so either message can win the race.
        Assert.Contains("http://127.0.0.1:9", status.Message);
    }

    [Fact]
    public async Task FindsWebcamUrlsOnceAndKeepsThem()
    {
        int calls = 0;
        var http = Stub(path =>
        {
            if (!path.StartsWith("/server/webcams/list")) return null;
            calls++;
            return """
                {"result":{"webcams":[{"enabled":false,"service":"mjpegstreamer","stream_url":"/off"},
                 {"enabled":true,"service":"mjpegstreamer","stream_url":"/webcam/?action=stream"}]}}
                """;
        });
        var backend = new MoonrakerBackend("p.local", http: http);
        var first = await backend.GetViewSourceAsync(CameraView.Main, default);
        var second = await backend.GetViewSourceAsync(CameraView.Main, default);
        Assert.Equal(new ViewSource("http://p.local/webcam/?action=stream", "http://p.local/webcam/?action=snapshot", null), first);
        Assert.Same(first, second);
        Assert.Equal(1, calls);
    }

    private static HttpClient Stub(Func<string, string?> respond, HttpStatusCode status = HttpStatusCode.OK) =>
        new(new StubHandler(respond, status));

    private sealed class StubHandler(Func<string, string?> respond, HttpStatusCode status) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            string? body = respond(request.RequestUri!.PathAndQuery);
            var response = status != HttpStatusCode.OK
                ? new HttpResponseMessage(status)
                : body == null
                    ? new HttpResponseMessage(HttpStatusCode.NotFound)
                    : new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
            return Task.FromResult(response);
        }
    }
}

public class BambuTests
{
    [Fact]
    public void ParsesReport()
    {
        var status = BambuBackend.ParseReport(JsonNode.Parse("""
            {"print": {"gcode_state": "RUNNING", "mc_percent": 42, "mc_remaining_time": 63,
                       "subtask_name": "Benchy", "gcode_file": "Benchy.3mf"}}
            """));
        Assert.Equal(new PrinterStatus("printing", 0.42, "Benchy", 63 * 60, "Slicer"), status);
    }

    [Fact]
    public void PrepareIsPreparing()
    {
        var status = BambuBackend.ParseReport(JsonNode.Parse("""{"print": {"gcode_state": "PREPARE", "mc_percent": 0}}"""));
        Assert.Equal("printing", status.State);
        Assert.True(status.Preparing);
    }

    [Fact]
    public async Task MergesPartialReports()
    {
        using var backend = new BambuBackend("192.0.2.1", "SERIAL", "code");
        backend.OnMessage(Encoding.UTF8.GetBytes("""{"print":{"gcode_state":"RUNNING","mc_percent":10,"subtask_name":"Cube"}}"""));
        backend.OnMessage(Encoding.UTF8.GetBytes("""{"print":{"mc_percent":55}}"""));
        backend.OnMessage(Encoding.UTF8.GetBytes("""{"info":{"not":"a print report"}}"""));
        var merged = backend.LatestReport()!;
        Assert.Equal("printing", merged.State);   // kept from the first report
        Assert.Equal("Cube", merged.Filename);    // kept from the first report
        Assert.Equal(0.55, merged.Progress);      // updated by the second
        // Never connected (not started), so the overall status is offline.
        Assert.Equal("offline", (await backend.GetStatusAsync(default)).State);
    }

    [Fact]
    public void BuildsRtspsUrl() =>
        Assert.Equal("rtsps://bblp:12345678@192.168.1.50:322/streaming/live/1",
                     BambuBackend.BuildRtspsUrl("192.168.1.50", "12345678").ToString());
}

public class MjpegTests
{
    [Fact]
    public void SplitsFramesAcrossChunkBoundaries()
    {
        byte[] stream = [.. "--b\r\n"u8, 0xFF, 0xD8, 1, 2, 3, 0xFF, 0xD9, .. "\r\n--b\r\n"u8, 0xFF, 0xD8, 4, 0xFF, 0xD9];
        var parser = new JpegFrameParser();
        var frames = new List<byte[]>();
        foreach (byte b in stream) // worst case: one byte at a time
            frames.AddRange(parser.Feed([b]));
        Assert.Equal(2, frames.Count);
        Assert.Equal([0xFF, 0xD8, 1, 2, 3, 0xFF, 0xD9], frames[0]);
        Assert.Equal([0xFF, 0xD8, 4, 0xFF, 0xD9], frames[1]);
    }
}

public class ConsoleLineTests
{
    [Theory]
    [InlineData("G28", "command", "> G28", false)]
    [InlineData("!! Move out of range", "response", "Move out of range", false)]
    [InlineData("// Unknown command:\"T0\"", "response", "Unknown command:\"T0\"", false)]
    [InlineData("echo: Nozzle expansion offset applied", "response", "Nozzle expansion offset applied", false)] // as Mainsail shows it
    [InlineData("B:60.0 /60.0 T0:200.0 /210.0", "response", "B:60.0 /60.0 T0:200.0 /210.0", true)]
    public void Classifies(string message, string type, string text, bool isTemperature)
    {
        var line = ConsoleLine.FromEntry(new ConsoleEntry(message, 1_700_000_000, type));
        Assert.Equal(text, line.Text);
        Assert.Equal(isTemperature, line.IsTemperature);
        Assert.True(line.HasTime);
    }

    [Fact]
    public void MultiLineResponsesLoseEveryMarker()
    {
        var line = ConsoleLine.FromEntry(new ConsoleEntry(
            "// Bed Mesh state has been saved to profile [wipe_tmp]\n// for the current session.\n// The SAVE_CONFIG command will", 1, "response"));
        Assert.Equal("Bed Mesh state has been saved to profile [wipe_tmp]\nfor the current session.\nThe SAVE_CONFIG command will", line.Text);
    }

    [Fact]
    public void ActionAndDebugLinesAreDimmedLikeMainsail()
    {
        var normal = ConsoleLine.FromEntry(new ConsoleEntry("// Klipper state: Ready", 1, "response"));
        var action = ConsoleLine.FromEntry(new ConsoleEntry("// action:cancel", 1, "response"));
        Assert.NotEqual(normal.Brush, action.Brush);
        Assert.Equal("action:cancel", action.Text);
    }
}

/// <summary>The console hides exactly what Mainsail (v2.19) hides.</summary>
public class ConsoleFilterTests
{
    private static ConsoleEntry Response(string message, double time = 1_700_000_000) => new(message, time, "response");
    private static ConsoleEntry Command(string message) => new(message, 1_700_000_000, "command");

    [Theory]
    [InlineData("B:60.0 /60.0 T0:200.0 /210.0", false)]       // temperature report
    [InlineData("ok B:60.0 /60.0 T0:200.0 /210.0", false)]
    [InlineData("T:210.0 /210.0", false)]
    [InlineData("C:35.0 /0.0", false)]
    [InlineData("// Bed Mesh state has been saved", true)]
    [InlineData("Bed heater on", true)]                       // "B" but no colon right after
    public void DefaultsHideTemperatureReports(string message, bool shown) =>
        Assert.Equal(shown, ConsoleFilter.MainsailDefaults.Shows(Response(message)));

    [Theory]
    [InlineData("SET_GCODE_VARIABLE MACRO=TIMELAPSE_TAKE_FRAME VARIABLE=takingframe VALUE=False", false)]
    [InlineData("TIMELAPSE_TAKE_FRAME", false)]
    [InlineData("_TIMELAPSE_NEW_FRAME HYPERLAPSE=FALSE", false)]
    [InlineData("TIMELAPSE_RENDER", false)]
    [InlineData("_SET_TIMELAPSE_SETUP ENABLE=1", false)]
    [InlineData("HYPERLAPSE ACTION=START CYCLE=30", false)]
    [InlineData("timelapse_take_frame", true)]               // Mainsail's rules are case-sensitive
    [InlineData("SET_GCODE_VARIABLE MACRO=PRINT_START VARIABLE=x VALUE=1", true)]
    [InlineData("G28", true)]
    public void DefaultsHideTimelapseCommands(string message, bool shown) =>
        Assert.Equal(shown, ConsoleFilter.MainsailDefaults.Shows(Command(message)));

    [Fact]
    public void FollowsMainsailSettings()
    {
        // Temperatures shown, timelapse still hidden, one custom filter on (two rules,
        // one of them broken, so skipped) and one off, and the console cleared at some point.
        using var settings = JsonDocument.Parse("""
            {"height":640,"hideWaitTemperatures":false,"cleared_since":1700000000000,
             "consolefilters":{
               "a":{"name":"Probe","bool":true,"regex":"^probe at\n([unclosed\n"},
               "b":{"name":"Off","bool":false,"regex":"^G28"}}}
            """);
        var filter = ConsoleFilter.FromMainsail(settings.RootElement);
        Assert.Equal(["^_TIMELAPSE_NEW_FRAME", "^TIMELAPSE_TAKE_FRAME", "^TIMELAPSE_RENDER", "^_SET_TIMELAPSE_SETUP",
                      "^HYPERLAPSE ACTION=", "^SET_GCODE_VARIABLE MACRO=TIMELAPSE_", "^probe at", "([unclosed"],
                     filter.Rules);
        Assert.True(filter.Shows(Response("B:60.0 /60.0 T0:200.0 /210.0", 1_700_000_001)));
        Assert.False(filter.Shows(Response("probe at 10.0,20.0 is z=1.2", 1_700_000_001)));
        Assert.True(filter.Shows(Response("G28", 1_700_000_001)));               // filter that's off
        Assert.False(filter.Shows(Response("from before the clear", 1_699_999_999)));
    }

    [Fact]
    public void NothingSavedMeansMainsailDefaults()
    {
        using var settings = JsonDocument.Parse("""{"height":640}""");
        Assert.True(ConsoleFilter.FromMainsail(settings.RootElement).SameAs(ConsoleFilter.MainsailDefaults));
        Assert.False(ConsoleFilter.MainsailDefaults.SameAs(ConsoleFilter.None));
    }
}

public class IconTests
{
    [Theory]
    [InlineData("printing")] [InlineData("paused")] [InlineData("complete")]
    [InlineData("error")] [InlineData("offline")] [InlineData("standby")]
    public void DrawsEveryStateAtAnySize(string state)
    {
        foreach (int size in new[] { 16, 20, 32, 64, 256 })
        {
            using var bitmap = TrayIconRenderer.Draw(state, 0.5, size);
            Assert.Equal(size, bitmap.Width);
            using var icon = TrayIconRenderer.DrawIcon(state, 0.5, size);
            Assert.Equal(size, icon.Width);
        }
    }

    // The top of the ring, where progress starts.
    private static System.Drawing.Color RingTop(string state, double progress, bool preparing)
    {
        using var bitmap = TrayIconRenderer.Draw(state, progress, 64, preparing);
        return bitmap.GetPixel(32, 7);
    }

    [Fact]
    public void AtZeroPercentTheRingIsADarkGreenTrack()
    {
        var top = RingTop("printing", 0, false);
        Assert.Equal(255, top.A); // opaque, so the taskbar's colour doesn't show through
        Assert.True(top.G > top.R && top.G > top.B && top.G < 150); // green, but dark
    }

    [Fact]
    public void AFewPercentStillShowsAStub()
    {
        // 15° clockwise from the top, on the ring: past 1% (3.6°), within the minimum stub.
        using var bitmap = TrayIconRenderer.Draw("printing", 0.01, 64);
        var p = bitmap.GetPixel(38, 9);
        Assert.True(p.G > 200, $"{p}");
    }

    [Fact]
    public void PreparingIsAWholeGoldRing()
    {
        var top = RingTop("printing", 0, true);
        Assert.Equal(255, top.A);
        Assert.True(top.R > 200 && top.G > 150 && top.B < 100);
    }
}
