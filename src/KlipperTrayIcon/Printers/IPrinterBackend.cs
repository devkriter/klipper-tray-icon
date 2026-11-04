using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace KlipperTrayIcon.Printers;

/// <summary>A printer we can watch. Constructors must not do network I/O - only Start() may.</summary>
public interface IPrinterBackend : IDisposable
{
    bool SupportsWebcam { get; }
    bool SupportsConsole { get; }
    /// <summary>Web interface to open from the tray menu, if the printer has one.</summary>
    string? DashboardUrl { get; }
    /// <summary>Short name for the popup's title bar (e.g. the printer's hostname).</summary>
    string DisplayName { get; }

    void Start();
    Task<PrinterStatus> GetStatusAsync(CancellationToken ct);
    /// <summary>For the settings window's Test button.</summary>
    Task<(bool Ok, string Message)> TestConnectionAsync(CancellationToken ct);

    /// <summary>Name of the second view the popup can switch to (e.g. the printer's touchscreen), or null.</summary>
    string? SecondViewName { get; }
    /// <summary>What to check when the second view can't be shown, or null.</summary>
    string? SecondViewHelp => null;

    /// <summary>
    /// How many screen pixels wide the popup shows the video (0: unknown).
    /// H.264 is converted no bigger than that needs; can change while playing.
    /// </summary>
    int VideoWidth { get; set; }

    /// <summary>Frames of the live camera until cancelled or the stream ends. The caller disposes each one.</summary>
    IAsyncEnumerable<CameraFrame> GetWebcamFramesAsync(CameraView view, CancellationToken ct);
    /// <summary>One JPEG (or PNG) if the printer can serve one cheaply, else null.</summary>
    Task<byte[]?> GetWebcamSnapshotAsync(CameraView view, TimeSpan timeout, CancellationToken ct);
    /// <summary>True once the view is known to take clicks (a remote touchscreen).</summary>
    bool IsTouchable(CameraView view);
    /// <summary>Taps or drags on a touchable view, in its pixel coordinates. Errors are ignored.</summary>
    Task SendTouchAsync(CameraView view, TouchAction action, int x, int y, CancellationToken ct);

    /// <summary>Up to <paramref name="count"/> recent console lines, oldest first. Throws if the printer can't be reached.</summary>
    Task<IReadOnlyList<ConsoleEntry>> GetConsoleMessagesAsync(int count, CancellationToken ct);
    /// <summary>Which console lines to hide (Mainsail's settings). Throws if the printer can't be reached.</summary>
    Task<ConsoleFilter> GetConsoleFilterAsync(CancellationToken ct) => Task.FromResult(ConsoleFilter.None);
    /// <summary>Restarts Klipper after it shut down (FIRMWARE_RESTART). Throws if it can't.</summary>
    Task RestartKlipperAsync(CancellationToken ct) => throw new NotSupportedException();
    /// <summary>Whether it has an emergency stop (Klipper's), for the popup's E-Stop button.</summary>
    bool SupportsEmergencyStop => false;
    /// <summary>Stops the printer at once: heaters and motors off, Klipper shut down. Throws if it can't.</summary>
    Task EmergencyStopAsync(CancellationToken ct) => throw new NotSupportedException();
    /// <summary>Human-readable reason for an exception thrown by this backend.</summary>
    string DescribeError(Exception e);
}

/// <summary>Which picture the popup shows: the camera, or the second view picked in Settings.</summary>
public enum CameraView { Main, Second }

public enum TouchAction { Down, Move, Up }

/// <summary>How a Klipper webcam's pictures are fetched, best first.</summary>
public enum StreamKind { Rtsp, H264, Mjpeg, Snapshots }

public static class StreamKinds
{
    /// <summary>The config value ("rtsp", "h264", "mjpeg", "snapshots"); empty means automatic.</summary>
    public static string ToConfig(StreamKind kind) => kind.ToString().ToLowerInvariant();

    public static StreamKind? Parse(string? value) =>
        Enum.TryParse<StreamKind>((value ?? "").Trim(), ignoreCase: true, out var kind) && Enum.IsDefined(kind) ? kind : null;

    public static string Label(StreamKind kind) => kind switch
    {
        StreamKind.Rtsp => "RTSP",
        StreamKind.H264 => "H.264",
        StreamKind.Mjpeg => "MJPEG",
        _ => "Snapshots",
    };
}

public static class PrinterBackends
{
    public static IPrinterBackend Create(AppConfig config) =>
        config.IsBambu
            ? new BambuBackend(config.BambuIp, config.BambuSerial, config.BambuAccessCode)
            : config.IsU1
                // Klipper, with the touchscreen as the second view (or a second camera).
                ? new MoonrakerBackend(config.MoonrakerUrl, config.MoonrakerApiKey,
                                       webcamName: config.WebcamName, webcamStream: config.WebcamStream,
                                       secondViewName: config.U1Touchscreen ? "" : config.SecondViewName,
                                       secondViewStream: config.U1Touchscreen ? "" : config.SecondViewStream,
                                       remoteScreen: config.U1Touchscreen, statsTiles: config.ShownStatsTiles)
                : new MoonrakerBackend(config.MoonrakerUrl, config.MoonrakerApiKey,
                                       webcamName: config.WebcamName, secondViewName: config.SecondViewName,
                                       webcamStream: config.WebcamStream, secondViewStream: config.SecondViewStream,
                                       statsTiles: config.ShownStatsTiles);

    /// <summary>Whether a U1 Extended serves its touchscreen (Remote Screen turned on), for the settings window.</summary>
    public static async Task<bool> HasTouchscreenAsync(AppConfig config, CancellationToken ct = default)
    {
        using var backend = new MoonrakerBackend(config.MoonrakerUrl, config.MoonrakerApiKey);
        return await backend.HasRemoteScreenAsync(ct);
    }

    /// <summary>The printer's temperature sensors besides nozzles and bed, for the settings window. Throws if it can't be reached.</summary>
    public static async Task<IReadOnlyList<string>> ListSensorsAsync(AppConfig config, CancellationToken ct = default)
    {
        if (config.IsBambu)
            return [];
        using var backend = new MoonrakerBackend(config.MoonrakerUrl, config.MoonrakerApiKey);
        return await backend.GetSensorsAsync(ct);
    }

    /// <summary>The printer's cameras, for the settings window. Throws if it can't be reached.</summary>
    public static async Task<IReadOnlyList<WebcamInfo>> ListWebcamsAsync(AppConfig config, CancellationToken ct = default)
    {
        if (config.IsBambu)
            return []; // one built-in camera, nothing to choose
        using var backend = new MoonrakerBackend(config.MoonrakerUrl, config.MoonrakerApiKey);
        return await backend.ListWebcamsAsync(ct);
    }

    /// <summary>The streams a camera offers, best first, for the settings window. Throws if it can't be reached.</summary>
    public static async Task<IReadOnlyList<StreamKind>> ListStreamsAsync(AppConfig config, string cameraName, CameraView view,
                                                                      CancellationToken ct = default)
    {
        if (config.IsBambu)
            return [];
        using var backend = new MoonrakerBackend(config.MoonrakerUrl, config.MoonrakerApiKey);
        return await backend.ListStreamsAsync(cameraName, view, ct);
    }

    /// <summary>Spins up a throwaway backend for config and reports (ok, message).</summary>
    public static async Task<(bool Ok, string Message)> TestConnectionAsync(AppConfig config, CancellationToken ct = default)
    {
        using var backend = Create(config);
        backend.Start();
        try { return await backend.TestConnectionAsync(ct); }
        catch (Exception e) { return (false, backend.DescribeError(e)); }
    }
}
