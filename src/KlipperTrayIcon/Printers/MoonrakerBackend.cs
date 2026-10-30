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
    private double? _cachedEstimatedTime;
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
            using var doc = await GetJsonAsync("/printer/objects/query?print_stats&display_status&virtual_sdcard",
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
                _cachedEstimatedTime = null;
                if (filename.Length > 0)
                {
                    try
                    {
                        using var meta = await GetJsonAsync(
                            "/server/files/metadata?filename=" + Uri.EscapeDataString(filename), TimeSpan.FromSeconds(2), ct);
                        double estimated = meta.RootElement.Prop("result").Num("estimated_time");
                        _cachedEstimatedTime = estimated > 0 ? estimated : null;
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
                if (_cachedEstimatedTime is double estimate)
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
            return new PrinterStatus(state, progress, filename, timeLeft, etaSource);
        }
        catch (Exception e) when (!ct.IsCancellationRequested)
        {
            return PrinterStatus.Offline(DescribeError(e));
        }
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

    public static double Num(this JsonElement e, string name, double fallback = 0)
    {
        var value = e.Prop(name);
        return value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out double d) ? d : fallback;
    }
}
