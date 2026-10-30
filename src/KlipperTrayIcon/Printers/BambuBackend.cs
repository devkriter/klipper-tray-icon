using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using MQTTnet;
using MQTTnet.Adapter;
using MQTTnet.Client;
using MQTTnet.Protocol;

namespace KlipperTrayIcon.Printers;

/// <summary>
/// Bambu Lab printers over their local MQTT interface (port 8883, user
/// "bblp", password = access code). No Bambu Cloud account is used, and the
/// printer doesn't need LAN Only Mode.
/// </summary>
public sealed class BambuBackend : IPrinterBackend
{
    // Wait this long after connecting for the first report before blaming
    // the config (a wrong serial number connects fine but gets no data).
    private static readonly TimeSpan FirstReportTimeout = TimeSpan.FromSeconds(25);
    private static readonly TimeSpan ReconnectDelay = TimeSpan.FromSeconds(5);

    private static readonly Dictionary<string, string> StateMap = new()
    {
        ["RUNNING"] = "printing",
        ["PREPARE"] = "printing",
        ["SLICING"] = "printing",
        ["PAUSE"] = "paused",
        ["FINISH"] = "complete",
        ["FAILED"] = "error",
        ["IDLE"] = "standby",
    };

    private readonly string _ip;
    private readonly string _serial;
    private readonly string _accessCode;
    private readonly int _port;
    private readonly int _cameraPort;
    private readonly object _lock = new();
    private readonly CancellationTokenSource _shutdown = new();
    private IMqttClient? _client;
    private bool _connected;
    private DateTime? _connectedAt;
    private MqttClientConnectResultCode? _connectResult;
    // Printers may send only the fields that changed after the initial full
    // report, so reports are merged into one running picture.
    private JsonObject? _print;

    public BambuBackend(string ip, string serial, string accessCode, int port = 8883, int cameraPort = 322)
    {
        _port = port;
        _cameraPort = cameraPort;
        _ip = (ip ?? "").Trim();
        _serial = (serial ?? "").Trim().ToUpperInvariant();
        _accessCode = (accessCode ?? "").Trim();
    }

    public bool SupportsWebcam => true;
    public bool SupportsConsole => false;
    public string? DashboardUrl => null;

    // ---- report parsing (community-documented schema; no official docs) ----

    public static PrinterStatus ParseReport(JsonNode? payload)
    {
        var print = payload?["print"] as JsonObject ?? payload as JsonObject ?? new JsonObject();
        string gcodeState = GetString(print, "gcode_state");
        string state = StateMap.GetValueOrDefault(gcodeState, "standby");
        double percent = GetNumber(print, "mc_percent");
        double remainingMinutes = GetNumber(print, "mc_remaining_time");
        string filename = GetString(print, "subtask_name");
        if (filename.Length == 0)
            filename = GetString(print, "gcode_file");
        return new PrinterStatus(state, percent / 100.0, filename, remainingMinutes * 60, "Slicer");
    }

    private static string GetString(JsonObject o, string name) =>
        o[name] is JsonValue v && v.TryGetValue(out string? s) ? s ?? "" : "";

    private static double GetNumber(JsonObject o, string name)
    {
        if (o[name] is not JsonValue v) return 0;
        if (v.TryGetValue(out double d)) return d;
        if (v.TryGetValue(out long l)) return l;
        if (v.TryGetValue(out string? s) && double.TryParse(s, System.Globalization.CultureInfo.InvariantCulture, out d)) return d;
        return 0;
    }

    // ---- MQTT connection ------------------------------------------------------

    public void Start()
    {
        var client = new MqttFactory().CreateMqttClient();
        client.ApplicationMessageReceivedAsync += e =>
        {
            OnMessage(e.ApplicationMessage.PayloadSegment);
            return Task.CompletedTask;
        };
        client.DisconnectedAsync += e =>
        {
            lock (_lock)
            {
                _connected = false;
                if (e.ConnectResult != null)
                    _connectResult = e.ConnectResult.ResultCode;
            }
            return Task.CompletedTask;
        };
        _client = client;
        _ = Task.Run(() => ConnectLoopAsync(client, _shutdown.Token));
    }

    private async Task ConnectLoopAsync(IMqttClient client, CancellationToken ct)
    {
        var options = new MqttClientOptionsBuilder()
            .WithTcpServer(_ip, _port)
            .WithCredentials("bblp", _accessCode)
            .WithClientId("klippertray-" + Guid.NewGuid().ToString("N")[..8])
            .WithKeepAlivePeriod(TimeSpan.FromSeconds(15))
            .WithTimeout(TimeSpan.FromSeconds(10))
            .WithTlsOptions(tls => tls
                .UseTls()
                // The printer uses a self-signed certificate.
                .WithCertificateValidationHandler(_ => true))
            .Build();

        while (!ct.IsCancellationRequested)
        {
            if (!client.IsConnected)
            {
                try
                {
                    var result = await client.ConnectAsync(options, ct);
                    lock (_lock)
                    {
                        _connectResult = result.ResultCode;
                        _connected = result.ResultCode == MqttClientConnectResultCode.Success;
                        _connectedAt = DateTime.UtcNow;
                    }
                    if (_connected)
                    {
                        await client.SubscribeAsync($"device/{_serial}/report", MqttQualityOfServiceLevel.AtMostOnce, ct);
                        // Ask for a full report straight away instead of waiting for changes.
                        await client.PublishStringAsync($"device/{_serial}/request",
                            """{"pushing": {"sequence_id": "0", "command": "pushall"}}""", cancellationToken: ct);
                    }
                }
                catch (MqttConnectingFailedException e)
                {
                    lock (_lock) { _connected = false; _connectResult = e.ResultCode; }
                }
                catch (Exception) when (!ct.IsCancellationRequested)
                {
                    lock (_lock) { _connected = false; }
                }
            }
            try { await Task.Delay(ReconnectDelay, ct); }
            catch (OperationCanceledException) { break; }
        }
    }

    internal void OnMessage(ArraySegment<byte> payload)
    {
        JsonNode? node;
        try { node = JsonNode.Parse(Encoding.UTF8.GetString(payload)); }
        catch (JsonException) { return; }
        if (node?["print"] is not JsonObject print)
            return;
        lock (_lock)
        {
            _print ??= new JsonObject();
            foreach (var (key, value) in print)
                _print[key] = value?.DeepClone();
        }
    }

    public void Dispose()
    {
        _shutdown.Cancel();
        var client = Interlocked.Exchange(ref _client, null);
        if (client != null)
        {
            _ = Task.Run(async () =>
            {
                try { await client.DisconnectAsync(); } catch (Exception) { }
                client.Dispose();
            });
        }
    }

    // ---- status ------------------------------------------------------------------

    private static bool IsAuthFailure(MqttClientConnectResultCode? rc) =>
        rc is MqttClientConnectResultCode.BadUserNameOrPassword or MqttClientConnectResultCode.NotAuthorized;

    /// <summary>Null if things look healthy (or it's too early to tell).</summary>
    private string? OfflineReason(TimeSpan firstReportTimeout)
    {
        lock (_lock)
        {
            if (IsAuthFailure(_connectResult))
                return "Printer rejected the access code";
            if (!_connected)
                return $"Can't reach printer at {_ip}";
            if (_print == null && _connectedAt is DateTime at && DateTime.UtcNow - at > firstReportTimeout)
                return "Connected, but no data from printer - check the serial number";
            return null;
        }
    }

    /// <summary>The merged report so far, or null if none has arrived.</summary>
    internal PrinterStatus? LatestReport()
    {
        lock (_lock)
            return _print == null ? null : ParseReport(new JsonObject { ["print"] = _print.DeepClone() });
    }

    public Task<PrinterStatus> GetStatusAsync(CancellationToken ct)
    {
        if (OfflineReason(FirstReportTimeout) is string reason)
            return Task.FromResult(PrinterStatus.Offline(reason));
        return Task.FromResult(LatestReport() ?? new PrinterStatus("standby", 0, Message: "Waiting for printer..."));
    }

    public async Task<(bool Ok, string Message)> TestConnectionAsync(CancellationToken ct)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(12);
        while (DateTime.UtcNow < deadline && !ct.IsCancellationRequested)
        {
            if (LatestReport() is { } report)
                return (true, $"Connected - printer is {StatusText.Label(report.State).ToLowerInvariant()}");
            lock (_lock)
            {
                if (IsAuthFailure(_connectResult))
                    return (false, "Printer rejected the access code");
            }
            await Task.Delay(200, ct);
        }
        return (false, OfflineReason(TimeSpan.Zero) ?? $"No response from printer at {_ip}");
    }

    public string DescribeError(Exception e) => $"Can't reach printer at {_ip}";

    public Task<IReadOnlyList<ConsoleEntry>> GetConsoleMessagesAsync(CancellationToken ct) =>
        throw new NotSupportedException("Bambu printers have no console");

    // ---- camera ----------------------------------------------------------------------

    /// <summary>
    /// The camera of printers that have RTSPS (X1/X2D/P2S/H2D/H2C/H2S): H.264
    /// over RTSP over TLS, user "bblp", password = access code.
    /// </summary>
    public static Uri BuildRtspsUrl(string ip, string accessCode, int port = 322) =>
        new($"rtsps://bblp:{Uri.EscapeDataString(accessCode)}@{ip}:{port}/streaming/live/1");

    public Task<byte[]?> GetWebcamSnapshotAsync(TimeSpan timeout, CancellationToken ct) =>
        Task.FromResult<byte[]?>(null); // would mean holding the printer's only camera slot; the popup keeps the last frame instead

    public async IAsyncEnumerable<CameraFrame> GetWebcamFramesAsync([EnumeratorCancellation] CancellationToken ct)
    {
        // Disposing the client (when the popup hides) hangs up at once: the
        // printer allows only one camera viewer.
        await using var rtsp = new RtspClient(BuildRtspsUrl(_ip, _accessCode, _cameraPort));
        await rtsp.StartAsync(ct);
        using var decoder = new H264Decoder();
        var depacketizer = new H264Depacketizer(rtsp.Video!.ParameterSets);
        var units = new List<AccessUnit>();
        await foreach (var packet in rtsp.ReadPacketsAsync(ct))
        {
            depacketizer.Push(packet, units);
            foreach (var unit in units)
            {
                List<PixelFrame> frames;
                try { frames = decoder.Decode(unit.Data, unit.Timestamp * 1000L / 9); } // 90 kHz -> 100 ns
                catch (COMException) { continue; } // a damaged picture; the next keyframe recovers
                foreach (var frame in frames)
                    yield return frame;
            }
            units.Clear();
        }
    }
}