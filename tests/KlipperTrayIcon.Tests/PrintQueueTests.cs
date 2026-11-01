using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using KlipperTrayIcon.Printers;
using KlipperTrayIcon.UI;
using Xunit;

namespace KlipperTrayIcon.Tests;

/// <summary>The print queue against Moonraker's job_queue API.</summary>
public class PrintQueueTests
{
    private static readonly byte[] Png = [0x89, (byte)'P', (byte)'N', (byte)'G', 0x0D, 0x0A, 0x1A, 0x0A, 1, 2, 3];
    private static readonly byte[] BigPng = [0x89, (byte)'P', (byte)'N', (byte)'G', 0x0D, 0x0A, 0x1A, 0x0A, 9, 9, 9];

    /// <summary>A fake Moonraker that records what it's asked ("METHOD path body").</summary>
    private sealed class FakeMoonraker : HttpMessageHandler
    {
        public readonly List<string> Requests = new();

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            string body = request.Content == null ? "" : " " + await request.Content.ReadAsStringAsync(ct);
            string path = Uri.UnescapeDataString(request.RequestUri!.PathAndQuery);
            lock (Requests)
                Requests.Add($"{request.Method} {path}{body}");
            string? json = path switch
            {
                "/server/info" => """{"result":{"components":["file_manager","job_queue","history"]}}""",
                "/server/job_queue/status" => """
                    {"result":{"queue_state":"paused","queued_jobs":[
                      {"filename":"Benchy.gcode","job_id":"0001","time_added":1,"time_in_queue":2},
                      {"filename":"parts/Clip.gcode","job_id":"0002","time_added":1,"time_in_queue":2}]}}
                    """,
                "/server/files/list?root=gcodes" => """
                    {"result":[{"path":"old.gcode","modified":100},{"path":"parts/Clip.gcode","modified":300},
                               {"path":"Benchy.gcode","modified":200}]}
                    """,
                "/server/files/metadata?filename=parts/Clip.gcode" => """
                    {"result":{"estimated_time":2280,"thumbnails":[
                      {"width":32,"relative_path":".thumbs/Clip-32x32.png"},
                      {"width":300,"relative_path":".thumbs/Clip-300x300.png"},
                      {"width":48,"relative_path":".thumbs/Clip-48x48.png"}]}}
                    """,
                _ => request.Method == HttpMethod.Get ? null : """{"result":"ok"}""",
            };
            if (path == "/server/files/gcodes/parts/.thumbs/Clip-48x48.png")
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(Png) };
            if (path == "/server/files/gcodes/parts/.thumbs/Clip-300x300.png")
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(BigPng) };
            return json == null
                ? new HttpResponseMessage(HttpStatusCode.NotFound)
                : new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
        }
    }

    private static (MoonrakerBackend Backend, FakeMoonraker Printer) Make()
    {
        var printer = new FakeMoonraker();
        return (new MoonrakerBackend("p", http: new HttpClient(printer)), printer);
    }

    [Fact]
    public async Task ReadsTheQueue()
    {
        var (backend, _) = Make();
        var status = await backend.GetQueueAsync(default);
        Assert.Equal("paused", status.State);
        Assert.Equal([new QueuedJob("0001", "Benchy.gcode"), new QueuedJob("0002", "parts/Clip.gcode")], status.Jobs);
        Assert.True(await backend.HasJobQueueAsync(default));
    }

    [Fact]
    public async Task RecentFilesAreNewestFirst()
    {
        var (backend, _) = Make();
        Assert.Equal(["parts/Clip.gcode", "Benchy.gcode"], (await backend.GetRecentFilesAsync(2, default)).Select(f => f.Path));
    }

    [Fact]
    public async Task FileDetailsHaveTheEstimateAndASmallThumbnail()
    {
        var (backend, printer) = Make();
        var details = await backend.GetFileDetailsAsync("parts/Clip.gcode", default);
        Assert.Equal(2280, details.EstimatedSeconds);
        Assert.Equal(Png, details.Thumbnail); // the 48 px one, from next to the file
        Assert.Equal(BigPng, details.Preview); // the 300 px one, for hovering
        await backend.GetFileDetailsAsync("parts/Clip.gcode", default);
        Assert.Single(printer.Requests, r => r.Contains("/server/files/metadata")); // cached
    }

    [Fact]
    public async Task ChangesGoToTheJobQueueApi()
    {
        var (backend, printer) = Make();
        await backend.AddToQueueAsync("parts/Clip.gcode", default);
        await backend.RemoveFromQueueAsync("0001", default);
        await backend.ReorderQueueAsync(["parts/Clip.gcode", "Benchy.gcode"], default);
        await backend.StartQueueAsync(default);
        await backend.StartPrintAsync("parts/Big part.gcode", default);
        Assert.Equal([
            """POST /server/job_queue/job {"filenames":["parts/Clip.gcode"]}""",
            "DELETE /server/job_queue/job?job_ids=0001",
            """POST /server/job_queue/job {"filenames":["parts/Clip.gcode","Benchy.gcode"],"reset":true}""",
            "POST /server/job_queue/start",
            "POST /printer/print/start?filename=parts/Big part.gcode", // the fake records it unescaped
        ], printer.Requests);
    }

    [Theory]
    [InlineData("""{"state":"ready","state_message":"Printer is ready"}""", null)]
    [InlineData("""{"state":"startup","state_message":"Printer is not ready"}""", null)]
    [InlineData("""{"state":"shutdown","state_message":"Lost communication with MCU 'mcu'\nOnce the underlying issue is corrected, use the \"FIRMWARE_RESTART\" command.\nPrinter is shutdown"}""",
                "Klipper has shut down: Lost communication with MCU 'mcu'")]
    [InlineData("""{"state":"shutdown","state_message":"Shutdown due to webhooks request."}""",
                "Klipper has shut down: Shutdown due to webhooks request")]
    [InlineData("""{"state":"error","state_message":"Option 'foo' is not valid in section 'printer'\n"}""",
                "Klipper couldn't start: Option 'foo' is not valid in section 'printer'")]
    [InlineData("""{"state":"shutdown"}""", "Klipper has shut down")]
    [InlineData("{}", null)]
    public void KlipperNeedingARestartSaysWhy(string webhooks, string? expected)
    {
        using var doc = JsonDocument.Parse(webhooks);
        Assert.Equal(expected, MoonrakerBackend.Halted(doc.RootElement));
    }

    [Theory]
    [InlineData("Klipper has shut down: Lost communication with MCU 'mcu'", "Shut down")]
    [InlineData("Klipper couldn't start: Option 'foo' is not valid", "Couldn't start")]
    public void KlipperNeedingARestartIsTheHeadline(string halted, string headline)
    {
        // Even mid-print: the print has stopped with it.
        var lines = StatusText.Lines(new PrinterStatus("printing", 0.4, "Benchy.gcode", Halted: halted));
        Assert.Equal([headline, halted], lines);
    }

    [Fact]
    public async Task RestartIsAFirmwareRestart()
    {
        var (backend, printer) = Make();
        await backend.RestartKlipperAsync(default);
        Assert.Equal(["POST /printer/firmware_restart"], printer.Requests);
    }

    [Fact]
    public async Task FailedChangesThrow()
    {
        var backend = new MoonrakerBackend("p", http: new HttpClient(new Refusing()));
        await Assert.ThrowsAsync<MoonrakerHttpException>(() => backend.StartQueueAsync(default));
    }

    private sealed class Refusing : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.BadRequest));
    }

    [Theory]
    [InlineData("""{"thumbnails":[{"width":32,"relative_path":"a"},{"width":300,"relative_path":"b"},{"width":48,"relative_path":"c"}]}""", "c")]
    [InlineData("""{"thumbnails":[{"width":16,"relative_path":"a"},{"width":32,"relative_path":"b"}]}""", "b")]
    [InlineData("""{}""", null)]
    public void PicksASmallThumbnail(string meta, string? expected)
    {
        using var doc = JsonDocument.Parse(meta);
        Assert.Equal(expected, MoonrakerBackend.PickThumbnail(doc.RootElement));
    }

    [Theory]
    [InlineData("""{"thumbnails":[{"width":32,"relative_path":"a"},{"width":300,"relative_path":"b"},{"width":48,"relative_path":"c"}]}""", "b")]
    [InlineData("""{"thumbnails":[{"width":48,"relative_path":"a"}]}""", "a")]
    [InlineData("""{}""", null)]
    public void PreviewIsTheBiggestThumbnail(string meta, string? expected)
    {
        using var doc = JsonDocument.Parse(meta);
        Assert.Equal(expected, MoonrakerBackend.PickPreview(doc.RootElement));
    }

    [Fact]
    public void RecentUploadsLeaveOutQueuedFiles()
    {
        var recent = new[] { "A.gcode", "B.gcode", "C.gcode", "D.gcode" }.Select((p, i) => new GcodeFile(p, 100 - i)).ToList();
        var status = new QueueStatus("paused", [new QueuedJob("1", "B.gcode")]);
        Assert.Equal(["A.gcode", "C.gcode"], WebcamPopup.RecentToShow(recent, status, 2).Select(f => f.Path));
        // Off the queue again, it's back.
        Assert.Equal(["A.gcode", "B.gcode"], WebcamPopup.RecentToShow(recent, new QueueStatus("paused", []), 2).Select(f => f.Path));
    }

    [Theory]
    [InlineData("ABCDE", "ABDEF")] // C added to the queue: F moves up
    [InlineData("ABCDE", "NABCD")] // a new upload
    [InlineData("ABDEF", "ABCDE")] // C back from the queue
    [InlineData("ABC", "CAB")]
    [InlineData("ABC", "")]
    [InlineData("", "ABC")]
    [InlineData("ABC", "XYZ")]
    public void ListsChangeOnlyTheRowsThatDid(string before, string after)
    {
        var rows = new System.Collections.ObjectModel.ObservableCollection<char>(before);
        int resets = 0, touched = 0;
        rows.CollectionChanged += (_, e) =>
        {
            if (e.Action == System.Collections.Specialized.NotifyCollectionChangedAction.Reset)
                resets++;
            touched++;
        };
        WebcamPopup.Replace(rows, after);
        Assert.Equal(after, string.Concat(rows));
        Assert.Equal(0, resets); // never rebuilt as a whole
        Assert.True(touched <= Math.Max(before.Length, after.Length));
    }

    [Fact]
    public void UnchangedListIsLeftAlone()
    {
        var rows = new System.Collections.ObjectModel.ObservableCollection<char>("ABC");
        rows.CollectionChanged += (_, _) => Assert.Fail("changed");
        WebcamPopup.Replace(rows, "ABC");
    }

    [Theory]
    [InlineData("Benchy.gcode", "Benchy")]
    [InlineData("parts/Clip_x4.GCODE", "Clip_x4")]
    [InlineData("notes.txt", "notes.txt")]
    public void ShowsFileNamesWithoutFolderOrExtension(string path, string name) =>
        Assert.Equal(name, WebcamPopup.DisplayName(path));

    [Theory]
    [InlineData(20, "just now")]
    [InlineData(150, "2 min ago")]
    [InlineData(3 * 3600 + 10, "3 h ago")]
    [InlineData(30 * 3600, "yesterday")]
    [InlineData(5 * 86400, "5 days ago")]
    public void SaysHowLongAgo(int seconds, string text)
    {
        var now = new DateTimeOffset(2026, 10, 31, 12, 0, 0, TimeSpan.Zero);
        Assert.Equal(text, WebcamPopup.Ago(now.AddSeconds(-seconds), now));
    }
}
