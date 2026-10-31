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
    private (string Stream, string Snapshot)? _webcamUrls;

    public MoonrakerBackend(string moonrakerUrl, string apiKey = "", HttpClient? http = null)
    {
        BaseUrl = NormalizeUrl(moonrakerUrl);
        ApiKey = (apiKey ?? "").Trim();
        // Only needed when Moonraker doesn't already trust this PC's address
        // (trusted_clients in moonraker.conf), e.g. over Tailscale/VPN.
        if (ApiKey.Length > 0)
            _headers["X-Api-Key"] = ApiKey;
        _http = http ?? SharedHttp;
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

    private string Absolute(string path) =>
        path.StartsWith("http", StringComparison.OrdinalIgnoreCase) ? path
        : path.StartsWith('/') ? BaseUrl + path
        : BaseUrl + "/" + path;

    /// <summary>
    /// Stream and snapshot URL of the first enabled MJPEG webcam. Cached after
    /// the first successful lookup - the list rarely changes, and skipping the
    /// round trip makes the popup faster.
    /// </summary>
    internal async Task<(string Stream, string Snapshot)> GetWebcamUrlsAsync(CancellationToken ct)
    {
        if (_webcamUrls is { } cached)
            return cached;
        var fallback = (BaseUrl + "/webcam/?action=stream", BaseUrl + "/webcam/?action=snapshot");
        JsonDocument doc;
        try { doc = await GetJsonAsync("/server/webcams/list", TimeSpan.FromSeconds(2), ct); }
        catch (Exception) when (!ct.IsCancellationRequested) { return fallback; } // not cached: retry next time

        using (doc)
        {
            var urls = fallback;
            var webcams = doc.RootElement.Prop("result").Prop("webcams");
            if (webcams.ValueKind == JsonValueKind.Array)
            {
                foreach (var cam in webcams.EnumerateArray())
                {
                    bool enabled = !cam.TryGetProperty("enabled", out var en) || en.ValueKind != JsonValueKind.False;
                    string service = cam.Str("service");
                    if (!enabled || service is not ("mjpegstreamer" or "mjpegstreamer-adaptive" or "ipstream"))
                        continue;
                    string stream = Absolute(cam.Str("stream_url"));
                    string snapshot = cam.Str("snapshot_url");
                    urls = (stream, snapshot.Length > 0 ? Absolute(snapshot) : stream.Replace("action=stream", "action=snapshot"));
                    break;
                }
            }
            _webcamUrls = urls;
            return urls;
        }
    }

    /// <summary>Don't hand the API key to a camera hosted somewhere else.</summary>
    private IReadOnlyDictionary<string, string>? HeadersFor(string url) =>
        url.StartsWith(BaseUrl, StringComparison.OrdinalIgnoreCase) ? _headers : null;

    public async IAsyncEnumerable<CameraFrame> GetWebcamFramesAsync([EnumeratorCancellation] CancellationToken ct)
    {
        var (stream, _) = await GetWebcamUrlsAsync(ct);
        await foreach (var frame in MjpegReader.ReadFramesAsync(_http, stream, HeadersFor(stream), ct))
            yield return new JpegFrame(frame);
    }

    public async Task<byte[]?> GetWebcamSnapshotAsync(TimeSpan timeout, CancellationToken ct)
    {
        try
        {
            var (_, snapshot) = await GetWebcamUrlsAsync(ct);
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(timeout);
            using var request = new HttpRequestMessage(HttpMethod.Get, snapshot);
            if (HeadersFor(snapshot) is { } headers)
                foreach (var (name, value) in headers)
                    request.Headers.TryAddWithoutValidation(name, value);
            using var response = await _http.SendAsync(request, cts.Token);
            if (!response.IsSuccessStatusCode)
                return null;
            byte[] bytes = await response.Content.ReadAsByteArrayAsync(cts.Token);
            return bytes.Length > 2 && bytes[0] == 0xFF && bytes[1] == 0xD8 ? bytes : null;
        }
        catch (Exception)
        {
            return null;
        }
    }
}

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
