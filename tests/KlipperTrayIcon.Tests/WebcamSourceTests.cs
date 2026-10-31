using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using KlipperTrayIcon.Printers;
using Xunit;

namespace KlipperTrayIcon.Tests;

/// <summary>Picking cameras from Moonraker's webcam list, and the views that aren't MJPEG.</summary>
public class WebcamSourceTests
{
    // What a Snapmaker U1 with paxx12's extended firmware lists: a WebRTC
    // camera (with MJPEG next to it) and the touchscreen as an iframe.
    private const string U1Webcams = """
        {"result":{"webcams":[
          {"name":"case","enabled":true,"service":"webrtc-camerastreamer",
           "stream_url":"/webcam/webrtc","snapshot_url":"/webcam/snapshot.jpg"},
          {"name":"gui","enabled":true,"service":"iframe",
           "stream_url":"/screen/","snapshot_url":"/screen/snapshot"}]}}
        """;

    private static readonly byte[] Png = [0x89, (byte)'P', (byte)'N', (byte)'G', 0x0D, 0x0A, 0x1A, 0x0A, 1, 2, 3];

    [Fact]
    public async Task ListsWebcams()
    {
        var backend = new MoonrakerBackend("p", http: U1Printer(new()));
        var webcams = await backend.ListWebcamsAsync(default);
        Assert.Equal([new WebcamInfo("case", "webrtc-camerastreamer", "/webcam/webrtc", "/webcam/snapshot.jpg"),
                      new WebcamInfo("gui", "iframe", "/screen/", "/screen/snapshot")], webcams);
    }

    [Fact]
    public async Task FindsMjpegNextToAWebRtcCamera()
    {
        var backend = new MoonrakerBackend("p", http: U1Printer(new()));
        var source = await backend.GetViewSourceAsync(CameraView.Main, default);
        Assert.Equal(new ViewSource("http://p/webcam/stream.mjpg", "http://p/webcam/snapshot.jpg", null), source);
        Assert.Null(backend.SecondViewName);
        Assert.Null(await backend.GetViewSourceAsync(CameraView.Second, default));
    }

    [Fact]
    public async Task PicksTheNamedCamera()
    {
        var http = Stub(r => r.RequestUri!.AbsolutePath == "/server/webcams/list" ? Json("""
            {"result":{"webcams":[{"name":"top","service":"mjpegstreamer","stream_url":"/top/?action=stream"},
                                  {"name":"Nozzle","service":"mjpegstreamer","stream_url":"/nozzle/?action=stream"}]}}
            """) : null);
        var backend = new MoonrakerBackend("p", http: http, webcamName: "nozzle");
        Assert.Equal("http://p/nozzle/?action=stream", (await backend.GetViewSourceAsync(CameraView.Main, default))!.Stream);
    }

    [Fact]
    public async Task RemoteScreenIsPolledAndTakesTaps()
    {
        var requests = new List<string>();
        var backend = new MoonrakerBackend("p", http: U1Printer(requests), secondViewName: "gui");
        Assert.Equal("gui", backend.SecondViewName);

        var source = await backend.GetViewSourceAsync(CameraView.Second, default);
        Assert.Equal(new ViewSource(null, "http://p/screen/snapshot", "http://p/screen/touch"), source);
        Assert.True(backend.IsTouchable(CameraView.Second));
        Assert.False(backend.IsTouchable(CameraView.Main));

        await foreach (var frame in backend.GetWebcamFramesAsync(CameraView.Second, default))
        {
            Assert.Equal(Png, Assert.IsType<ImageFrame>(frame).Data);
            break;
        }

        await backend.SendTouchAsync(CameraView.Second, TouchAction.Down, 10, 20, default);
        await backend.SendTouchAsync(CameraView.Second, TouchAction.Up, 10, 20, default);
        Assert.Equal(["POST /screen/touch?a=down&x=10&y=20", "POST /screen/touch?a=up&x=10&y=20"],
                     requests.Where(r => r.StartsWith("POST")));
    }

    [Fact]
    public async Task UnchangedScreenIsNotSentAgain()
    {
        int fetches = 0;
        var http = Stub(r =>
        {
            if (r.RequestUri!.AbsolutePath != "/screen/snapshot")
                return r.RequestUri.AbsolutePath == "/server/webcams/list" ? Json(U1Webcams) : null;
            Interlocked.Increment(ref fetches);
            if (r.Headers.TryGetValues("If-None-Match", out var tags) && tags.Contains("\"v1\""))
                return new HttpResponseMessage(HttpStatusCode.NotModified);
            var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(Png) };
            response.Headers.ETag = new EntityTagHeaderValue("\"v1\"");
            return response;
        });
        var backend = new MoonrakerBackend("p", http: http, secondViewName: "gui");
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(800));
        int frames = 0;
        try
        {
            await foreach (var frame in backend.GetWebcamFramesAsync(CameraView.Second, cts.Token))
                frames++;
        }
        catch (OperationCanceledException) { }
        Assert.Equal(1, frames);
        Assert.True(fetches > 3, $"only {fetches} fetches"); // still polling, just nothing new to show
    }

    [Fact]
    public async Task ProbesAgainAfterNetworkErrors()
    {
        bool down = true;
        var http = Stub(r => r.RequestUri!.AbsolutePath switch
        {
            "/server/webcams/list" => Json(U1Webcams),
            "/webcam/stream.mjpg" when down => throw new HttpRequestException("unreachable"),
            "/webcam/stream.mjpg" => Mjpeg(),
            _ => null,
        });
        var backend = new MoonrakerBackend("p", http: http);
        Assert.Null((await backend.GetViewSourceAsync(CameraView.Main, default))!.Stream); // not known to be MJPEG yet...
        down = false;
        Assert.Equal("http://p/webcam/stream.mjpg", (await backend.GetViewSourceAsync(CameraView.Main, default))!.Stream); // ...so not cached
    }

    [Theory]
    [InlineData("http://p/webcam/webrtc", "http://p/webcam/")]
    [InlineData("http://p/screen/", "http://p/screen/")]
    [InlineData("http://p/webcam/?action=stream", "http://p/webcam/")]
    public void FindsTheFolder(string url, string folder) => Assert.Equal(folder, MoonrakerBackend.Folder(url));

    // ---- a fake U1 -----------------------------------------------------------

    private static HttpClient U1Printer(List<string> requests) => Stub(r =>
    {
        lock (requests)
            requests.Add($"{r.Method} {r.RequestUri!.PathAndQuery}");
        return (r.Method.Method, r.RequestUri!.AbsolutePath) switch
        {
            ("GET", "/server/webcams/list") => Json(U1Webcams),
            ("GET", "/webcam/stream.mjpg") => Mjpeg(),
            ("GET", "/screen/") => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("<script>fetch('touch?a=' + action)</script>", Encoding.UTF8, "text/html"),
            },
            ("GET", "/screen/snapshot") => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(Png) },
            ("POST", "/screen/touch") => new HttpResponseMessage(HttpStatusCode.OK),
            _ => null,
        };
    });

    private static HttpResponseMessage Json(string body) =>
        new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private static HttpResponseMessage Mjpeg()
    {
        var content = new ByteArrayContent([]);
        content.Headers.ContentType = MediaTypeHeaderValue.Parse("multipart/x-mixed-replace; boundary=frame");
        return new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
    }

    /// <summary>Null answers 404.</summary>
    private static HttpClient Stub(Func<HttpRequestMessage, HttpResponseMessage?> respond) => new(new StubHandler(respond));

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage?> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            Task.FromResult(respond(request) ?? new HttpResponseMessage(HttpStatusCode.NotFound));
    }
}
