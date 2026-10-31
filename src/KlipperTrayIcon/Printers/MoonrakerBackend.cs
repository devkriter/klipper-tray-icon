using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace KlipperTrayIcon.Printers;

/// <summary>Klipper via Moonraker's HTTP API (what Mainsail and Fluidd talk to).</summary>
public sealed class MoonrakerBackend : IPrinterBackend
{
    private static readonly HttpClient SharedHttp = new(new SocketsHttpHandler
    {
        PooledConnectionLifetime = TimeSpan.FromMinutes(5),
        ConnectTimeout = TimeSpan.FromSeconds(4),
    })
    { Timeout = Timeout.InfiniteTimeSpan };

    private readonly HttpClient _http;
    private readonly Dictionary<string, string> _headers = new();
    private string? _lastFilename;
    private GcodeFileInfo _fileInfo = GcodeFileInfo.None;
    private readonly string _webcamName, _secondViewName;
    private readonly ViewSource?[] _views = new ViewSource?[2]; // by CameraView

    public MoonrakerBackend(string moonrakerUrl, string apiKey = "", HttpClient? http = null,
                            string webcamName = "", string secondViewName = "")
    {
        BaseUrl = NormalizeUrl(moonrakerUrl);
        ApiKey = (apiKey ?? "").Trim();
        // Only needed when Moonraker doesn't already trust this PC's address
        // (trusted_clients in moonraker.conf), e.g. over Tailscale/VPN.
        if (ApiKey.Length > 0)
            _headers["X-Api-Key"] = ApiKey;
        _http = http ?? SharedHttp;
        _webcamName = (webcamName ?? "").Trim();
        _secondViewName = (secondViewName ?? "").Trim();
    }

    public string BaseUrl { get; }
    public string ApiKey { get; }
    internal IReadOnlyDictionary<string, string> Headers => _headers;

    public bool SupportsWebcam => true;
    public bool SupportsConsole => true;
    public string? DashboardUrl => BaseUrl;

    /// <summary>Accepts what people actually type ("mainsail.local", "192.168.1.5/").</summary>
    public static string NormalizeUrl(string? url)
    {
        url = (url ?? "").Trim().TrimEnd('/');
        if (url.Length > 0 && !url.Contains("://"))
            url = "http://" + url;
        return url;
    }

    public void Start() { }
    public void Dispose() { }

    // ---- HTTP helpers ----------------------------------------------------

    private async Task<JsonDocument> GetJsonAsync(string pathAndQuery, TimeSpan timeout, CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);
        using var request = new HttpRequestMessage(HttpMethod.Get, BaseUrl + pathAndQuery);
        foreach (var (name, value) in _headers)
            request.Headers.TryAddWithoutValidation(name, value);
        try
        {
            using var response = await _http.SendAsync(request, cts.Token);
            if (!response.IsSuccessStatusCode)
                throw new MoonrakerHttpException(response.StatusCode);
            await using var body = await response.Content.ReadAsStreamAsync(cts.Token);
            return await JsonDocument.ParseAsync(body, cancellationToken: cts.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new TimeoutException($"{BaseUrl} didn't respond in time");
        }
    }

    public string DescribeError(Exception e) => e switch
    {
        TimeoutException => $"{BaseUrl} didn't respond in time",
        MoonrakerHttpException { StatusCode: HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden } =>
            ApiKey.Length > 0 ? "Moonraker rejected the API key" : "Moonraker refused access - enter an API key in Settings",
        MoonrakerHttpException h => $"{BaseUrl} returned HTTP {(int)h.StatusCode}",
        HttpRequestException => $"Can't reach {BaseUrl}",
        _ => $"Unexpected response from {BaseUrl}",
    };

    // ---- status ------------------------------------------------------------

    public async Task<PrinterStatus> GetStatusAsync(CancellationToken ct)
    {
        try
        {
            using var doc = await GetJsonAsync(
                "/printer/objects/query?print_stats&display_status&virtual_sdcard" +
                "&extruder=temperature,target&heater_bed=temperature,target&gcode_move=gcode_position",
                TimeSpan.FromSeconds(2), ct);
            var status = doc.RootElement.Prop("result").Prop("status");
            var printStats = status.Prop("print_stats");

            string state = printStats.Str("state", "standby");
            // virtual_sdcard progress is usually the more accurate one.
            double progress = status.Prop("virtual_sdcard").Num("progress");
            if (progress == 0)
                progress = status.Prop("display_status").Num("progress");
            string filename = printStats.Str("filename");
            double printDuration = printStats.Num("print_duration");

            // The slicer's estimate (from the file's metadata) gives a far
            // better ETA than extrapolating from progress.
            if (filename != _lastFilename)
            {
                _lastFilename = filename;
                _fileInfo = GcodeFileInfo.None;
                if (filename.Length > 0)
                {
                    try
                    {
                        using var meta = await GetJsonAsync(
                            "/server/files/metadata?filename=" + Uri.EscapeDataString(filename), TimeSpan.FromSeconds(2), ct);
                        _fileInfo = GcodeFileInfo.From(meta.RootElement.Prop("result"));
                    }
                    catch (Exception) when (!ct.IsCancellationRequested)
                    {
                        // No metadata: fall back to the calculated ETA.
                    }
                }
            }

            double timeLeft = 0;
            string etaSource = "Calc";
            if (state == "printing")
            {
                if (_fileInfo.EstimatedTime is double estimate)
                {
                    timeLeft = Math.Max(0, estimate - printDuration);
                    etaSource = "Slicer";
                }
                if ((timeLeft == 0 || etaSource == "Calc") && progress > 0.01)
                {
                    timeLeft = printDuration / progress - printDuration;
                    etaSource = "Calc";
                }
            }
            return new PrinterStatus(state, progress, filename, timeLeft, etaSource,
                                     Details: ReadDetails(status, state, _fileInfo));
        }
        catch (Exception e) when (!ct.IsCancellationRequested)
        {
            return PrinterStatus.Offline(DescribeError(e));
        }
    }

    private static PrintDetails ReadDetails(JsonElement status, string state, GcodeFileInfo file)
    {
        static Heater? ReadHeater(JsonElement h) =>
            h.ValueKind == JsonValueKind.Object ? new Heater(h.Num("temperature"), h.Num("target")) : null;

        var position = status.Prop("gcode_move").Prop("gcode_position");
        double z = position.ValueKind == JsonValueKind.Array && position.GetArrayLength() > 2
                   && position[2].TryGetDouble(out double pz) ? pz : 0;
        int? layer = null, total = null;
        if (state is "printing" or "paused")
        {
            // Klipper knows the layer when the slicer calls SET_PRINT_STATS_INFO;
            // otherwise work it out from Z and the file's layer heights, like Mainsail does.
            var info = status.Prop("print_stats").Prop("info");
            total = Positive(info.Opt("total_layer")) ?? file.LayerCount;
            layer = Positive(info.Opt("current_layer")) ?? file.LayerAt(z);
            if (layer != null && total != null)
                layer = Math.Min(layer.Value, total.Value);
        }
        return new PrintDetails(ReadHeater(status.Prop("extruder")), ReadHeater(status.Prop("heater_bed")), layer, total, z);
    }

    private static int? Positive(double? value) => value > 0 ? (int)value : null;

    /// <summary>What we use from a G-code file's metadata.</summary>
    private sealed record GcodeFileInfo(double? EstimatedTime, int? LayerCount, double LayerHeight, double FirstLayerHeight)
    {
        public static readonly GcodeFileInfo None = new(null, null, 0, 0);

        public static GcodeFileInfo From(JsonElement meta)
        {
            double layerHeight = meta.Num("layer_height"), first = meta.Num("first_layer_height", layerHeight);
            int? count = Positive(meta.Opt("layer_count"));
            double height = meta.Num("object_height");
            if (count == null && layerHeight > 0 && height > 0)
                count = (int)Math.Ceiling((height - first) / layerHeight + 1 - 1e-6);
            return new GcodeFileInfo(meta.Num("estimated_time") is > 0 and var t ? t : null, count, layerHeight, first);
        }

        public int? LayerAt(double z) =>
            LayerHeight > 0 && z > 0 ? Math.Max(1, (int)Math.Ceiling((z - FirstLayerHeight) / LayerHeight + 1 - 1e-6)) : null;
    }

    public async Task<(bool Ok, string Message)> TestConnectionAsync(CancellationToken ct)
    {
        try
        {
            using var doc = await GetJsonAsync("/printer/info", TimeSpan.FromSeconds(4), ct);
            var info = doc.RootElement.Prop("result");
            string host = info.Str("hostname", BaseUrl);
            string klippy = info.Str("state", "unknown");
            return klippy == "ready"
                ? (true, $"Connected to {host}")
                : (true, $"Connected to {host}, but Klipper is '{klippy}'");
        }
        catch (Exception e) when (!ct.IsCancellationRequested)
        {
            return (false, DescribeError(e));
        }
    }

    // ---- console -------------------------------------------------------------

    public async Task<IReadOnlyList<ConsoleEntry>> GetConsoleMessagesAsync(CancellationToken ct)
    {
        using var doc = await GetJsonAsync("/server/gcode_store?count=100", TimeSpan.FromSeconds(3), ct);
        var store = doc.RootElement.Prop("result").Prop("gcode_store");
        if (store.ValueKind != JsonValueKind.Array)
            return [];
        return store.EnumerateArray()
            .Select(e => new ConsoleEntry(e.Str("message"), e.Num("time"), e.Str("type", "response")))
            .ToList();
    }

    // ---- webcam --------------------------------------------------------------

    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(100);
    // A polled view that hasn't changed still sends a frame this often, so the
    // popup doesn't take a still touchscreen for a stalled stream.
    private static readonly TimeSpan PollKeepAlive = TimeSpan.FromSeconds(5);

    public string? SecondViewName => _secondViewName.Length > 0 ? _secondViewName : null;

    private string Absolute(string path) =>
        path.StartsWith("http", StringComparison.OrdinalIgnoreCase) ? path
        : path.StartsWith('/') ? BaseUrl + path
        : BaseUrl + "/" + path;

    /// <summary>The folder a URL points into ("http://p/webcam/webrtc" → "http://p/webcam/").</summary>
    internal static string Folder(string url)
    {
        int end = url.IndexOfAny(['?', '#']);
        if (end >= 0)
            url = url[..end];
        return url[..(url.LastIndexOf('/') + 1)];
    }

    private static bool IsMjpegService(string service) =>
        service is "mjpegstreamer" or "mjpegstreamer-adaptive" or "uv4l-mjpeg" or "ipstream";

    /// <summary>Every webcam set up in Mainsail/Fluidd. Throws if Moonraker can't be reached.</summary>
    public async Task<IReadOnlyList<WebcamInfo>> ListWebcamsAsync(CancellationToken ct)
    {
        using var doc = await GetJsonAsync("/server/webcams/list", TimeSpan.FromSeconds(3), ct);
        var webcams = doc.RootElement.Prop("result").Prop("webcams");
        if (webcams.ValueKind != JsonValueKind.Array)
            return [];
        return webcams.EnumerateArray()
            .Select(cam => new WebcamInfo(
                cam.Str("name"), cam.Str("service"), cam.Str("stream_url"), cam.Str("snapshot_url"),
                Enabled: !cam.TryGetProperty("enabled", out var en) || en.ValueKind != JsonValueKind.False))
            .ToList();
    }

    /// <summary>
    /// How to show a view, or null if it isn't set up. Cached after the first
    /// successful lookup - the list rarely changes, and skipping the round
    /// trips makes the popup faster.
    /// </summary>
    internal async Task<ViewSource?> GetViewSourceAsync(CameraView view, CancellationToken ct)
    {
        if (_views[(int)view] is { } cached)
            return cached;
        string wanted = view == CameraView.Main ? _webcamName : _secondViewName;
        if (view == CameraView.Second && wanted.Length == 0)
            return null;
        var fallback = view == CameraView.Main
            ? new ViewSource(BaseUrl + "/webcam/?action=stream", BaseUrl + "/webcam/?action=snapshot", null)
            : null;

        IReadOnlyList<WebcamInfo> webcams;
        try { webcams = await ListWebcamsAsync(ct); }
        catch (Exception) when (!ct.IsCancellationRequested) { return fallback; } // not cached: retry next time

        var cam = wanted.Length > 0
            ? webcams.FirstOrDefault(c => string.Equals(c.Name, wanted, StringComparison.OrdinalIgnoreCase))
            : null;
        if (cam == null && view == CameraView.Main)
        {
            // The first camera, preferring one that streams MJPEG. Iframes are
            // web pages (like a remote screen), only shown when picked by name.
            var usable = webcams.Where(c => c.Enabled && c.Service != "iframe").ToList();
            cam = usable.FirstOrDefault(c => IsMjpegService(c.Service)) ?? usable.FirstOrDefault();
        }
        var (source, certain) = cam == null ? (fallback, true) : await ResolveAsync(cam, ct);
        if (certain)
            _views[(int)view] = source;
        return source;
    }

    /// <summary>
    /// The MJPEG stream of a webcam, or failing that its snapshot to poll.
    /// WebRTC/HLS/iframe views need a browser, but the servers behind them
    /// (camera-streamer, paxx12's firmware for the Snapmaker U1) usually serve
    /// MJPEG next to them. Not certain (so not worth caching) when a probe got
    /// no answer at all.
    /// </summary>
    private async Task<(ViewSource Source, bool Certain)> ResolveAsync(WebcamInfo cam, CancellationToken ct)
    {
        string stream = Absolute(cam.StreamUrl);
        string snapshot = cam.SnapshotUrl.Length > 0 ? Absolute(cam.SnapshotUrl) : stream.Replace("action=stream", "action=snapshot");
        if (IsMjpegService(cam.Service))
            return (new ViewSource(stream, snapshot, null), true);

        bool certain = true;
        string folder = Folder(stream);
        string[] candidates = stream.Contains("action=stream")
            ? [stream]
            : [folder + "stream.mjpg", folder + "stream", folder + "?action=stream"];
        foreach (string candidate in candidates)
        {
            bool? mjpeg = await IsMjpegAsync(candidate, ct);
            if (mjpeg == true)
                return (new ViewSource(candidate, snapshot, null), true);
            certain &= mjpeg != null;
        }

        // No stream: poll the snapshot. paxx12's remote screen also takes taps,
        // which its page sends to "touch" next to it.
        bool? touch = await PageMentionsAsync(stream, "touch?a=", ct);
        certain &= touch != null;
        return (new ViewSource(null, snapshot, touch == true ? folder + "touch" : null), certain);
    }

    private HttpRequestMessage Request(HttpMethod method, string url)
    {
        var request = new HttpRequestMessage(method, url);
        if (HeadersFor(url) is { } headers)
            foreach (var (name, value) in headers)
                request.Headers.TryAddWithoutValidation(name, value);
        return request;
    }

    /// <summary>Don't hand the API key to a camera hosted somewhere else.</summary>
    private IReadOnlyDictionary<string, string>? HeadersFor(string url) =>
        url.StartsWith(BaseUrl, StringComparison.OrdinalIgnoreCase) ? _headers : null;

    /// <summary>Whether the URL serves MJPEG; null if it couldn't be asked.</summary>
    private async Task<bool?> IsMjpegAsync(string url, CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(3));
        try
        {
            using var request = Request(HttpMethod.Get, url);
            // Headers only: disposing the response hangs up before any frames come.
            using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cts.Token);
            return response.IsSuccessStatusCode
                   && response.Content.Headers.ContentType?.MediaType?.StartsWith("multipart/", StringComparison.OrdinalIgnoreCase) == true;
        }
        catch (Exception) when (!ct.IsCancellationRequested)
        {
            return null;
        }
    }

    /// <summary>Whether the web page at the URL contains the text; null if it couldn't be fetched.</summary>
    private async Task<bool?> PageMentionsAsync(string url, string text, CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(3));
        try
        {
            using var request = Request(HttpMethod.Get, url);
            using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cts.Token);
            if (!response.IsSuccessStatusCode || response.Content.Headers.ContentType?.MediaType != "text/html")
                return false; // not a page (maybe a stream): don't download it
            return (await response.Content.ReadAsStringAsync(cts.Token)).Contains(text, StringComparison.Ordinal);
        }
        catch (Exception) when (!ct.IsCancellationRequested)
        {
            return null;
        }
    }

    public async IAsyncEnumerable<CameraFrame> GetWebcamFramesAsync(CameraView view, [EnumeratorCancellation] CancellationToken ct)
    {
        if (await GetViewSourceAsync(view, ct) is not { } source)
            yield break;
        var frames = source.Stream is string stream
            ? MjpegReader.ReadFramesAsync(_http, stream, HeadersFor(stream), ct)
            : PollSnapshotsAsync(source.Snapshot, ct);
        await foreach (var frame in frames)
            yield return new ImageFrame(frame);
    }

    /// <summary>
    /// A view without a stream, as its snapshot fetched about ten times a
    /// second. An unchanged picture (HTTP 304) isn't sent again, except now and
    /// then as a sign of life. Ends after a few failures in a row.
    /// </summary>
    private async IAsyncEnumerable<byte[]> PollSnapshotsAsync(string url, [EnumeratorCancellation] CancellationToken ct)
    {
        string? etag = null;
        byte[]? last = null;
        int failures = 0;
        var sinceSent = System.Diagnostics.Stopwatch.StartNew();
        while (!ct.IsCancellationRequested)
        {
            var started = System.Diagnostics.Stopwatch.StartNew();
            var (ok, image, newEtag) = await FetchImageAsync(url, etag, TimeSpan.FromSeconds(2), ct);
            if (!ok)
            {
                if (++failures >= 3)
                    yield break;
            }
            else
            {
                failures = 0;
                if (image != null)
                    (last, etag) = (image, newEtag);
                if (image != null || (last != null && sinceSent.Elapsed >= PollKeepAlive))
                {
                    sinceSent.Restart();
                    yield return image ?? last!;
                }
            }
            var wait = PollInterval - started.Elapsed;
            if (wait <= TimeSpan.Zero)
                continue;
            try { await Task.Delay(wait, ct); }
            catch (OperationCanceledException) { yield break; }
        }
    }

    /// <summary>
    /// One picture. Ok without an image means it hasn't changed since the
    /// given ETag; not ok if it couldn't be fetched or isn't a JPEG or PNG.
    /// </summary>
    private async Task<(bool Ok, byte[]? Image, string? ETag)> FetchImageAsync(string url, string? etag, TimeSpan timeout, CancellationToken ct)
    {
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(timeout);
            using var request = Request(HttpMethod.Get, url);
            if (etag != null)
                request.Headers.TryAddWithoutValidation("If-None-Match", etag);
            using var response = await _http.SendAsync(request, cts.Token);
            if (response.StatusCode == HttpStatusCode.NotModified)
                return (true, null, etag);
            if (!response.IsSuccessStatusCode)
                return (false, null, null);
            byte[] bytes = await response.Content.ReadAsByteArrayAsync(cts.Token);
            return IsImage(bytes) ? (true, bytes, response.Headers.ETag?.ToString()) : (false, null, null);
        }
        catch (Exception)
        {
            return (false, null, null);
        }
    }

    private static bool IsImage(byte[] bytes) =>
        bytes.Length > 4 && ((bytes[0] == 0xFF && bytes[1] == 0xD8)                                       // JPEG
                             || (bytes[0] == 0x89 && bytes[1] == 'P' && bytes[2] == 'N' && bytes[3] == 'G')); // PNG

    public async Task<byte[]?> GetWebcamSnapshotAsync(CameraView view, TimeSpan timeout, CancellationToken ct)
    {
        try
        {
            if (await GetViewSourceAsync(view, ct) is not { } source)
                return null;
            return (await FetchImageAsync(source.Snapshot, null, timeout, ct)).Image;
        }
        catch (Exception)
        {
            return null;
        }
    }

    public bool IsTouchable(CameraView view) => _views[(int)view]?.Touch != null;

    public async Task SendTouchAsync(CameraView view, TouchAction action, int x, int y, CancellationToken ct)
    {
        if (_views[(int)view]?.Touch is not string touch)
            return;
        string a = action switch { TouchAction.Down => "down", TouchAction.Move => "move", _ => "up" };
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(2));
            using var request = Request(HttpMethod.Post, $"{touch}?a={a}&x={x}&y={y}");
            using var response = await _http.SendAsync(request, cts.Token);
        }
        catch (Exception)
        {
            // A lost tap: the screen just doesn't react, like a flaky touchscreen.
        }
    }
}

/// <summary>A camera from Moonraker's list (set up in Mainsail/Fluidd).</summary>
public sealed record WebcamInfo(string Name, string Service, string StreamUrl, string SnapshotUrl, bool Enabled = true);

/// <summary>Where a view's pictures come from: an MJPEG stream if it has one, else its snapshot, polled.</summary>
internal sealed record ViewSource(string? Stream, string Snapshot, string? Touch);

public sealed class MoonrakerHttpException(HttpStatusCode statusCode)
    : Exception($"HTTP {(int)statusCode}")
{
    public HttpStatusCode StatusCode { get; } = statusCode;
}

/// <summary>Forgiving JSON accessors: missing or mistyped fields read as defaults.</summary>
internal static class JsonExtensions
{
    public static JsonElement Prop(this JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var value) ? value : default;

    public static string Str(this JsonElement e, string name, string fallback = "")
    {
        var value = e.Prop(name);
        return value.ValueKind == JsonValueKind.String ? value.GetString() ?? fallback : fallback;
    }

    /// <summary>The number, or null if it's missing or not a number (e.g. JSON null).</summary>
    public static double? Opt(this JsonElement e, string name)
    {
        var value = e.Prop(name);
        return value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out double d) ? d : null;
    }

    public static double Num(this JsonElement e, string name, double fallback = 0)
    {
        var value = e.Prop(name);
        return value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out double d) ? d : fallback;
    }
}
