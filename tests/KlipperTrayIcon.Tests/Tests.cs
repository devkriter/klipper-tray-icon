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
        var first = await backend.GetWebcamUrlsAsync(default);
        var second = await backend.GetWebcamUrlsAsync(default);
        Assert.Equal(("http://p.local/webcam/?action=stream", "http://p.local/webcam/?action=snapshot"), first);
        Assert.Equal(first, second);
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
    [InlineData("echo: Nozzle expansion offset applied", "response", "echo: Nozzle expansion offset applied", false)]
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
    public void ConsecutiveTemperatureReportsCollapseToOneLine()
    {
        var lines = new List<ConsoleLine>();
        ConsoleLine Entry(string m, string type = "response") => ConsoleLine.FromEntry(new ConsoleEntry(m, 1, type));

        ConsoleLine.AppendTo(lines, Entry("M190 S110", "command"));
        for (int i = 0; i < 30; i++)
            ConsoleLine.AppendTo(lines, Entry($"B:{100 + i * 0.3:0.0} /110.0 T0:{200 + i}.0 /250.0"));
        ConsoleLine.AppendTo(lines, Entry("// heating done"));
        ConsoleLine.AppendTo(lines, Entry("B:110.0 /110.0 T0:250.0 /250.0"));

        Assert.Equal(["> M190 S110", "B:108.7 /110.0 T0:229.0 /250.0", "heating done", "B:110.0 /110.0 T0:250.0 /250.0"],
                     lines.Select(l => l.Text));
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
}
