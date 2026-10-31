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
public sealed class MoonrakerBackend : IPrinterBackend, IPrintQueue
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
    // The extruder in use ("extruder", "extruder1", ...), from the last poll: on
    // toolchangers (Snapmaker U1) and IDEX printers the nozzle tile follows it.
    private string _activeExtruder = "extruder";
    private bool _multiTool;
    private readonly string _webcamName, _secondViewName;
    private readonly StreamKind?[] _preferredStream = new StreamKind?[2]; // by CameraView; null: automatic
    private readonly ViewSource?[] _views = new ViewSource?[2]; // by CameraView

    public MoonrakerBackend(string moonrakerUrl, string apiKey = "", HttpClient? http = null,
                            string webcamName = "", string secondViewName = "",
                            string webcamStream = "", string secondViewStream = "", bool remoteScreen = false)
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
        _preferredStream[(int)CameraView.Main] = StreamKinds.Parse(webcamStream);
        _preferredStream[(int)CameraView.Second] = StreamKinds.Parse(secondViewStream);
        _remoteScreen = remoteScreen;
    }

    // Snapmaker U1 (paxx12's extended firmware): the second view is the printer's
    // touchscreen, served as a snapshot at /screen/snapshot that takes taps at /screen/touch.
    private readonly bool _remoteScreen;

    public string BaseUrl { get; }
    public string ApiKey { get; }
    internal IReadOnlyDictionary<string, string> Headers => _headers;

    public bool SupportsWebcam => true;
    public bool SupportsConsole => true;
    public string? DashboardUrl => BaseUrl;
    public int VideoWidth { get; set; }

    /// <summary>The address's host until the printer has told us its hostname.</summary>
    public string DisplayName => _hostname ?? (Uri.TryCreate(BaseUrl, UriKind.Absolute, out var uri) ? uri.Host : BaseUrl);
    private volatile string? _hostname;
    private bool _askedHostname;

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
                $"&{_activeExtruder}=temperature,target&toolhead=extruder" +
                "&heater_bed=temperature,target&gcode_move=gcode_position",
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
            if (!_askedHostname)
            {
                _askedHostname = true;
                try
                {
                    using var info = await GetJsonAsync("/printer/info", TimeSpan.FromSeconds(2), ct);
                    string hostname = info.RootElement.Prop("result").Str("hostname");
                    if (hostname.Length > 0)
                        _hostname = hostname;
                }
                catch (Exception) when (!ct.IsCancellationRequested) { } // keep showing the address
            }
            return new PrinterStatus(state, progress, filename, timeLeft, etaSource,
                                     Details: ReadDetails(status, state, _fileInfo));
        }
        catch (Exception e) when (!ct.IsCancellationRequested)
        {
            return PrinterStatus.Offline(DescribeError(e));
        }
    }

    private PrintDetails ReadDetails(JsonElement status, string state, GcodeFileInfo file)
    {
        // Read the extruder asked for this time; ask for the one now in use next time.
        string asked = _activeExtruder;
        string active = status.Prop("toolhead").Str("extruder", asked);
        if (System.Text.RegularExpressions.Regex.IsMatch(active, "^extruder[0-9]*$"))
            _activeExtruder = active;
        _multiTool |= active != "extruder";
        string? tool = _multiTool ? "T" + (asked == "extruder" ? "0" : asked["extruder".Length..]) : null;

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
        return new PrintDetails(ReadHeater(status.Prop(asked)), ReadHeater(status.Prop("heater_bed")), layer, total, z, tool);
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

    // ---- print queue (Moonraker's job_queue) ------------------------------------

    private readonly Dictionary<string, GcodeDetails> _details = new();

    /// <summary>Whether this Moonraker has the job queue (it's an optional component). Throws if unreachable.</summary>
    public async Task<bool> HasJobQueueAsync(CancellationToken ct)
    {
        using var doc = await GetJsonAsync("/server/info", TimeSpan.FromSeconds(3), ct);
        var components = doc.RootElement.Prop("result").Prop("components");
        return components.ValueKind == JsonValueKind.Array
               && components.EnumerateArray().Any(c => c.GetString() == "job_queue");
    }

    public async Task<QueueStatus> GetQueueAsync(CancellationToken ct)
    {
        using var doc = await GetJsonAsync("/server/job_queue/status", TimeSpan.FromSeconds(3), ct);
        var result = doc.RootElement.Prop("result");
        var jobs = result.Prop("queued_jobs");
        return new QueueStatus(result.Str("queue_state", "paused"),
            jobs.ValueKind != JsonValueKind.Array ? []
                : jobs.EnumerateArray().Select(j => new QueuedJob(j.Str("job_id"), j.Str("filename"))).ToList());
    }

    public async Task<IReadOnlyList<GcodeFile>> GetRecentFilesAsync(int count, CancellationToken ct)
    {
        using var doc = await GetJsonAsync("/server/files/list?root=gcodes", TimeSpan.FromSeconds(5), ct);
        var files = doc.RootElement.Prop("result");
        if (files.ValueKind != JsonValueKind.Array)
            return [];
        var recent = files.EnumerateArray()
            .Select(f => new GcodeFile(f.Str("path"), f.Num("modified")))
            .Where(f => f.Path.Length > 0)
            .OrderByDescending(f => f.Modified)
            .Take(count)
            .ToList();
        lock (_details)
        {
            foreach (var f in recent)
            {
                // Uploaded again under the same name: its details are out of date.
                if (_detailsModified.TryGetValue(f.Path, out double seen) && seen != f.Modified)
                    _details.Remove(f.Path);
                _detailsModified[f.Path] = f.Modified;
            }
        }
        return recent;
    }

    // When each file's cached details were for (its modified time).
    private readonly Dictionary<string, double> _detailsModified = new();

    public async Task<GcodeDetails> GetFileDetailsAsync(string path, CancellationToken ct)
    {
        lock (_details)
            if (_details.TryGetValue(path, out var cached))
                return cached;
        using var doc = await GetJsonAsync("/server/files/metadata?filename=" + Uri.EscapeDataString(path),
                                           TimeSpan.FromSeconds(3), ct);
        var meta = doc.RootElement.Prop("result");
        // Thumbnails sit next to the file (e.g. ".thumbs/name-48x48.png").
        string folder = path.Contains('/') ? path[..(path.LastIndexOf('/') + 1)] : "";
        async Task<byte[]?> Fetch(string? relative) => relative == null ? null
            : (await FetchImageAsync(BaseUrl + "/server/files/gcodes/" +
                                     string.Join("/", (folder + relative).Split('/').Select(Uri.EscapeDataString)),
                                     null, TimeSpan.FromSeconds(3), ct)).Image;
        string? small = PickThumbnail(meta), big = PickPreview(meta);
        byte[]? thumbnail = await Fetch(small);
        byte[]? preview = big == small ? thumbnail : await Fetch(big);
        var details = new GcodeDetails(meta.Num("estimated_time") is > 0 and var t ? t : null, thumbnail, preview);
        lock (_details)
            _details[path] = details;
        return details;
    }

    /// <summary>The smallest thumbnail at least 48 px wide (the list shows them at 40), else the biggest.</summary>
    internal static string? PickThumbnail(JsonElement meta)
    {
        var thumbs = meta.Prop("thumbnails");
        if (thumbs.ValueKind != JsonValueKind.Array)
            return null;
        var all = thumbs.EnumerateArray()
            .Select(t => (Width: t.Num("width"), Path: t.Str("relative_path")))
            .Where(t => t.Path.Length > 0)
            .ToList();
        var fitting = all.Where(t => t.Width >= 48).OrderBy(t => t.Width).ToList();
        return fitting.Count > 0 ? fitting[0].Path : all.OrderByDescending(t => t.Width).Select(t => t.Path).FirstOrDefault();
    }

    /// <summary>The biggest thumbnail, shown when hovering over the small one.</summary>
    internal static string? PickPreview(JsonElement meta)
    {
        var thumbs = meta.Prop("thumbnails");
        return thumbs.ValueKind != JsonValueKind.Array ? null
            : thumbs.EnumerateArray()
                .Select(t => (Width: t.Num("width"), Path: t.Str("relative_path")))
                .Where(t => t.Path.Length > 0)
                .OrderByDescending(t => t.Width)
                .Select(t => t.Path)
                .FirstOrDefault();
    }

    public Task AddToQueueAsync(string path, CancellationToken ct) =>
        SendAsync(HttpMethod.Post, "/server/job_queue/job", new { filenames = new[] { path } }, ct);

    public Task RemoveFromQueueAsync(string jobId, CancellationToken ct) =>
        SendAsync(HttpMethod.Delete, "/server/job_queue/job?job_ids=" + Uri.EscapeDataString(jobId), null, ct);

    public Task ReorderQueueAsync(IReadOnlyList<string> paths, CancellationToken ct) =>
        SendAsync(HttpMethod.Post, "/server/job_queue/job", new { filenames = paths, reset = true }, ct);

    public Task StartQueueAsync(CancellationToken ct) =>
        SendAsync(HttpMethod.Post, "/server/job_queue/start", null, ct);

    /// <summary>A request that changes something on the printer. Throws unless it succeeds.</summary>
    private async Task SendAsync(HttpMethod method, string pathAndQuery, object? json, CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(5));
        using var request = Request(method, BaseUrl + pathAndQuery);
        if (json != null)
            request.Content = new StringContent(JsonSerializer.Serialize(json), System.Text.Encoding.UTF8, "application/json");
        try
        {
            using var response = await _http.SendAsync(request, cts.Token);
            if (!response.IsSuccessStatusCode)
                throw new MoonrakerHttpException(response.StatusCode);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new TimeoutException($"{BaseUrl} didn't respond in time");
        }
    }

    // ---- console -------------------------------------------------------------

    public async Task<IReadOnlyList<ConsoleEntry>> GetConsoleMessagesAsync(int count, CancellationToken ct)
    {
        using var doc = await GetJsonAsync($"/server/gcode_store?count={count}", TimeSpan.FromSeconds(3), ct);
        var store = doc.RootElement.Prop("result").Prop("gcode_store");
        if (store.ValueKind != JsonValueKind.Array)
            return [];
        return store.EnumerateArray()
            .Select(e => new ConsoleEntry(e.Str("message"), e.Num("time"), e.Str("type", "response")))
            .ToList();
    }

    /// <summary>
    /// Mainsail's console settings (what it hides), so the popup matches it.
    /// None saved (or no Mainsail): Mainsail's defaults.
    /// </summary>
    public async Task<ConsoleFilter> GetConsoleFilterAsync(CancellationToken ct)
    {
        try
        {
            using var doc = await GetJsonAsync("/server/database/item?namespace=mainsail&key=console", TimeSpan.FromSeconds(3), ct);
            return ConsoleFilter.FromMainsail(doc.RootElement.Prop("result").Prop("value"));
        }
        catch (MoonrakerHttpException e) when (e.StatusCode == HttpStatusCode.NotFound)
        {
            return ConsoleFilter.MainsailDefaults;
        }
    }

    // ---- webcam --------------------------------------------------------------

    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(100);
    // Automatic gives up on H.264 for MJPEG after this many drops or refusals within the window.
    private const int H264TroubleLimit = 3;
    private static readonly TimeSpan H264TroubleWindow = TimeSpan.FromSeconds(30);
    private readonly List<DateTime>[] _h264Trouble = [new(), new()]; // by CameraView
    // After switching to MJPEG, try the better stream again this much later: the trouble
    // may have been a passing outage (a camera service restarting itself takes 10-20 s).
    private static readonly TimeSpan RetryBetterStreamAfter = TimeSpan.FromMinutes(5);
    private readonly ViewSource?[] _betterStream = new ViewSource?[2]; // by CameraView
    private readonly DateTime[] _retryBetterAt = new DateTime[2];
    // A polled view that hasn't changed still sends a frame this often, so the
    // popup doesn't take a still touchscreen for a stalled stream.
    private static readonly TimeSpan PollKeepAlive = TimeSpan.FromSeconds(5);

    public string? SecondViewName => _remoteScreen ? "Touchscreen" : _secondViewName.Length > 0 ? _secondViewName : null;

    public string? SecondViewHelp => _remoteScreen
        ? "Turn on Remote Screen in the printer's firmware settings (/firmware-config)"
        : null;

    /// <summary>
    /// Whether a webcam list looks like a Snapmaker U1 with paxx12's firmware (its
    /// touchscreen is an iframe at /screen/), for Settings to suggest the U1 type.
    /// </summary>
    public static bool LooksLikeU1(IReadOnlyList<WebcamInfo> webcams) =>
        webcams.Any(c => c.Service == "iframe" && c.StreamUrl.TrimStart('/').StartsWith("screen", StringComparison.OrdinalIgnoreCase));

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
        if (_betterStream[(int)view] is { } better && DateTime.UtcNow >= _retryBetterAt[(int)view])
        {
            _views[(int)view] = better;
            _betterStream[(int)view] = null;
            lock (_h264Trouble[(int)view])
                _h264Trouble[(int)view].Clear();
        }
        if (_views[(int)view] is { } cached)
            return cached;
        if (view == CameraView.Second && _remoteScreen)
            return _views[(int)view] = new ViewSource(null, BaseUrl + "/screen/snapshot", BaseUrl + "/screen/touch",
                                                       StreamKind.Snapshots);
        string wanted = view == CameraView.Main ? _webcamName : _secondViewName;
        if (view == CameraView.Second && wanted.Length == 0)
            return null;
        var fallback = view == CameraView.Main
            ? new ViewSource(BaseUrl + "/webcam/?action=stream", BaseUrl + "/webcam/?action=snapshot", null)
            : null;

        IReadOnlyList<WebcamInfo> webcams;
        try { webcams = await ListWebcamsAsync(ct); }
        catch (Exception) when (!ct.IsCancellationRequested) { return fallback; } // not cached: retry next time

        var cam = PickCamera(webcams, wanted, view);
        var (source, certain) = cam == null ? (fallback, true) : await ResolveAsync(cam, _preferredStream[(int)view], ct);
        if (certain)
            _views[(int)view] = source;
        return source;
    }

    /// <summary>
    /// The webcam a view shows: the one named in Settings, or for the camera
    /// with none named, the first one (preferring MJPEG). Iframes are web
    /// pages (like a remote screen), only shown when picked by name.
    /// </summary>
    public static WebcamInfo? PickCamera(IReadOnlyList<WebcamInfo> webcams, string name, CameraView view)
    {
        var cam = name.Length > 0
            ? webcams.FirstOrDefault(c => string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase))
            : null;
        if (cam == null && view == CameraView.Main)
        {
            var usable = webcams.Where(c => c.Enabled && c.Service != "iframe").ToList();
            cam = usable.FirstOrDefault(c => IsMjpegService(c.Service)) ?? usable.FirstOrDefault();
        }
        return cam;
    }

    /// <summary>The streams a webcam offers, best first (for Settings). Throws if Moonraker can't be reached.</summary>
    public async Task<IReadOnlyList<StreamKind>> ListStreamsAsync(string cameraName, CameraView view, CancellationToken ct)
    {
        if (PickCamera(await ListWebcamsAsync(ct), cameraName, view) is not { } cam)
            return [];
        var (streams, _) = await DiscoverAsync(cam, ct);
        return streams.Select(s => s.Kind).ToList();
    }

    /// <summary>
    /// How to show a webcam: the stream picked in Settings if it offers it,
    /// else the best one it has. Not certain (so not worth caching) when a
    /// probe got no answer at all.
    /// </summary>
    private async Task<(ViewSource Source, bool Certain)> ResolveAsync(WebcamInfo cam, StreamKind? preferred, CancellationToken ct)
    {
        var (streams, certain) = await DiscoverAsync(cam, ct);
        var pick = streams.FirstOrDefault(s => s.Kind == preferred) ?? streams[0];
        string snapshot = streams[^1].Url; // snapshots are always last
        if (pick.Kind == StreamKind.Snapshots)
            return (new ViewSource(null, snapshot, null, StreamKind.Snapshots), certain);
        // Automatic RTSP or H.264 keeps MJPEG in reserve, in case they turn out unreliable.
        string? fallback = preferred == null && pick.Kind is StreamKind.Rtsp or StreamKind.H264
            ? streams.FirstOrDefault(s => s.Kind == StreamKind.Mjpeg)?.Url
            : null;
        return (new ViewSource(pick.Url, snapshot, null, pick.Kind, fallback), certain);
    }

    /// <summary>
    /// Every way a webcam can be shown, best first: RTSP (camera-streamer's
    /// RTSP server, if it's turned on), H.264 (camera-streamer serves its
    /// hardware-encoded video as video.h264; both are a fraction of MJPEG's
    /// bandwidth), MJPEG, and polling its snapshot, which is always offered. WebRTC/HLS/iframe views need a browser, but the servers behind
    /// them (camera-streamer, paxx12's firmware for the Snapmaker U1) usually
    /// serve one of these next to them.
    /// </summary>
    internal async Task<(List<StreamChoice> Streams, bool Certain)> DiscoverAsync(WebcamInfo cam, CancellationToken ct)
    {
        string stream = Absolute(cam.StreamUrl);
        string snapshot = cam.SnapshotUrl.Length > 0 ? Absolute(cam.SnapshotUrl) : stream.Replace("action=stream", "action=snapshot");
        string folder = Folder(stream);

        string h264Url = stream.EndsWith(".h264", StringComparison.OrdinalIgnoreCase) ? stream : folder + "video.h264";
        var rtsp = FindRtspAsync(folder, ct);
        var h264 = IsH264Async(h264Url, ct);
        var mjpeg = FindMjpegAsync(cam, stream, folder, ct);
        await Task.WhenAll(rtsp, h264, mjpeg);

        var streams = new List<StreamChoice>();
        if (rtsp.Result.Url is string rtspUrl)
            streams.Add(new StreamChoice(StreamKind.Rtsp, rtspUrl));
        if (h264.Result == true)
            streams.Add(new StreamChoice(StreamKind.H264, h264Url));
        if (mjpeg.Result.Url is string mjpegUrl)
            streams.Add(new StreamChoice(StreamKind.Mjpeg, mjpegUrl));
        streams.Add(new StreamChoice(StreamKind.Snapshots, snapshot));
        bool certain = rtsp.Result.Certain && h264.Result != null && mjpeg.Result.Certain;
        return (streams, certain);
    }

    /// <summary>
    /// camera-streamer's RTSP server, if it's on: its status page (next to the
    /// stream) says so. The address it gives is the name the printer's own web
    /// server uses for it (e.g. "mjpgstreamer1"), so the printer's address
    /// replaces it; then the port has to answer.
    /// </summary>
    private async Task<(string? Url, bool Certain)> FindRtspAsync(string folder, CancellationToken ct)
    {
        JsonDocument status;
        try { status = await GetJsonAsync(RelativeToBase(folder + "status"), TimeSpan.FromSeconds(3), ct); }
        catch (Exception e) when (e is MoonrakerHttpException or JsonException) { return (null, true); } // not camera-streamer
        catch (Exception) when (!ct.IsCancellationRequested) { return (null, false); }
        using (status)
        {
            var endpoint = status.RootElement.Prop("endpoints").Prop("rtsp");
            if (endpoint.Prop("enabled").ValueKind != JsonValueKind.True)
                return (null, true);
            string host = new Uri(BaseUrl).Host;
            var builder = Uri.TryCreate(endpoint.Str("uri"), UriKind.Absolute, out var given) && given.Scheme == "rtsp"
                ? new UriBuilder(given)
                : new UriBuilder("rtsp", host, 8554, "/stream.h264"); // camera-streamer's defaults
            builder.Host = host;
            bool? open = await PortAnswersAsync(builder.Host, builder.Port, ct);
            return (open == true ? builder.Uri.ToString() : null, open != null);
        }
    }

    /// <summary>The path and query of a URL on the printer, for GetJsonAsync (which adds BaseUrl).</summary>
    private string RelativeToBase(string url) =>
        url.StartsWith(BaseUrl, StringComparison.OrdinalIgnoreCase) ? url[BaseUrl.Length..] : new Uri(url).PathAndQuery;

    /// <summary>True if something listens there, false if the connection is refused, null if no answer.</summary>
    private static async Task<bool?> PortAnswersAsync(string host, int port, CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(2));
        using var tcp = new System.Net.Sockets.TcpClient();
        try
        {
            await tcp.ConnectAsync(host, port, cts.Token);
            return true;
        }
        catch (System.Net.Sockets.SocketException e) when (e.SocketErrorCode == System.Net.Sockets.SocketError.ConnectionRefused)
        {
            return false;
        }
        catch (Exception) when (!ct.IsCancellationRequested)
        {
            return null;
        }
    }

    private async Task<(string? Url, bool Certain)> FindMjpegAsync(WebcamInfo cam, string stream, string folder, CancellationToken ct)
    {
        if (IsMjpegService(cam.Service))
            return (stream, true);
        bool certain = true;
        string[] candidates = stream.Contains("action=stream")
            ? [stream]
            : [folder + "stream.mjpg", folder + "stream", folder + "?action=stream"];
        foreach (string candidate in candidates)
        {
            bool? mjpeg = await IsMjpegAsync(candidate, ct);
            if (mjpeg == true)
                return (candidate, true);
            certain &= mjpeg != null;
        }
        return (null, certain);
    }

    /// <summary>Whether the URL serves raw H.264 (starts with an Annex B start code); null if it couldn't be asked.</summary>
    private async Task<bool?> IsH264Async(string url, CancellationToken ct)
    {
        // camera-streamer sometimes answers 500 for a moment; ask twice before deciding.
        for (int attempt = 0; ; attempt++)
        {
            var (found, serverError) = await ProbeH264Async(url, ct);
            if (!serverError || attempt == 1)
                return found;
            await Task.Delay(300, ct);
        }
    }

    private async Task<(bool? Found, bool ServerError)> ProbeH264Async(string url, CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(3));
        try
        {
            using var request = Request(HttpMethod.Get, url);
            using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cts.Token);
            if ((int)response.StatusCode >= 500)
                return (false, true);
            if (!response.IsSuccessStatusCode || response.Content.Headers.ContentType?.MediaType?.StartsWith("text/") == true)
                return (false, false);
            await using var body = await response.Content.ReadAsStreamAsync(cts.Token);
            var head = new byte[8];
            int read = 0, n;
            while (read < head.Length && (n = await body.ReadAsync(head.AsMemory(read), cts.Token)) > 0)
                read += n;
            int start = head.AsSpan(0, read).IndexOf((ReadOnlySpan<byte>)[0, 0, 1]);
            return (start is >= 0 and <= 1, false);
        }
        catch (Exception) when (!ct.IsCancellationRequested)
        {
            return (null, false);
        }
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

    public async IAsyncEnumerable<CameraFrame> GetWebcamFramesAsync(CameraView view, [EnumeratorCancellation] CancellationToken ct)
    {
        if (await GetViewSourceAsync(view, ct) is not { } source)
            yield break;
        if (source is { Kind: StreamKind.Rtsp or StreamKind.H264, Stream: string video })
        {
            var trouble = _h264Trouble[(int)view];
            while (!ct.IsCancellationRequested)
            {
                int pictures = 0;
                var pictureStream = source.Kind == StreamKind.Rtsp
                    ? EndQuietly(RtspVideo.ReadFramesAsync(new Uri(video), () => VideoWidth, ct))
                    : ReadH264Async(video, ct);
                await foreach (var picture in pictureStream)
                {
                    pictures++;
                    yield return picture;
                }
                if (ct.IsCancellationRequested)
                    break;
                lock (trouble)
                {
                    trouble.Add(DateTime.UtcNow);
                    trouble.RemoveAll(t => DateTime.UtcNow - t > H264TroubleWindow);
                    if (source.Fallback is string mjpeg && trouble.Count >= H264TroubleLimit)
                    {
                        // Automatic, and RTSP/H.264 keeps failing: MJPEG for a while.
                        _betterStream[(int)view] = source;
                        _retryBetterAt[(int)view] = DateTime.UtcNow + RetryBetterStreamAfter;
                        _views[(int)view] = source with { Stream = mjpeg, Kind = StreamKind.Mjpeg, Fallback = null };
                        yield break;
                    }
                }
                // Refused (camera-streamer answers 500 at times): let the popup wait and retry.
                if (pictures == 0)
                    yield break;
                // Hung up mid-stream, which camera-streamer's video.h264 does every few
                // seconds on some printers, and a flaky camera can do to either. A new
                // connection starts with a keyframe, so carry straight on.
            }
            yield break;
        }
        var frames = source.Stream is string stream
            ? MjpegReader.ReadFramesAsync(_http, stream, HeadersFor(stream), ct)
            : PollSnapshotsAsync(source.Snapshot, ct);
        await foreach (var frame in frames)
            yield return new ImageFrame(frame);
    }

    /// <summary>A stream of pictures that ends, rather than throws, when it fails.</summary>
    private static async IAsyncEnumerable<CameraFrame> EndQuietly(IAsyncEnumerable<CameraFrame> frames)
    {
        await using var e = frames.GetAsyncEnumerator();
        while (true)
        {
            try
            {
                if (!await e.MoveNextAsync())
                    yield break;
            }
            catch (Exception)
            {
                yield break;
            }
            yield return e.Current;
        }
    }

    /// <summary>A raw H.264 stream (camera-streamer's video.h264), decoded by Windows.</summary>
    private async IAsyncEnumerable<CameraFrame> ReadH264Async(string url, [EnumeratorCancellation] CancellationToken ct)
    {
        using var decoder = new H264Decoder();
        var splitter = new AnnexBSplitter();
        var clock = System.Diagnostics.Stopwatch.StartNew();
        await foreach (var chunk in StreamDownload.ReadChunksAsync(_http, url, HeadersFor(url), ct))
        {
            foreach (var unit in splitter.Feed(chunk))
            {
                List<PixelFrame> pictures;
                decoder.TargetWidth = VideoWidth;
                try { pictures = decoder.Decode(unit, clock.Elapsed.Ticks); }
                catch (System.Runtime.InteropServices.COMException) { continue; } // a damaged picture; the next keyframe recovers
                foreach (var picture in pictures)
                    yield return picture;
            }
        }
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

/// <summary>Where a view's pictures come from: a stream (H.264 or MJPEG), else its snapshot, polled.</summary>
internal sealed record ViewSource(string? Stream, string Snapshot, string? Touch, StreamKind Kind = StreamKind.Mjpeg,
                                  string? Fallback = null);

/// <summary>One way a webcam can be shown; for snapshots, Url is the snapshot.</summary>
internal sealed record StreamChoice(StreamKind Kind, string Url);

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
