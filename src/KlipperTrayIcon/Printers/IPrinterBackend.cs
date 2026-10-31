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

    /// <summary>Frames of the live camera until cancelled or the stream ends. The caller disposes each one.</summary>
    IAsyncEnumerable<CameraFrame> GetWebcamFramesAsync(CameraView view, CancellationToken ct);
    /// <summary>One JPEG (or PNG) if the printer can serve one cheaply, else null.</summary>
    Task<byte[]?> GetWebcamSnapshotAsync(CameraView view, TimeSpan timeout, CancellationToken ct);
    /// <summary>True once the view is known to take clicks (a remote touchscreen).</summary>
    bool IsTouchable(CameraView view);
    /// <summary>Taps or drags on a touchable view, in its pixel coordinates. Errors are ignored.</summary>
    Task SendTouchAsync(CameraView view, TouchAction action, int x, int y, CancellationToken ct);

    /// <summary>Recent console lines, oldest first. Throws if the printer can't be reached.</summary>
    Task<IReadOnlyList<ConsoleEntry>> GetConsoleMessagesAsync(CancellationToken ct);
    /// <summary>Human-readable reason for an exception thrown by this backend.</summary>
    string DescribeError(Exception e);
}

/// <summary>Which picture the popup shows: the camera, or the second view picked in Settings.</summary>
public enum CameraView { Main, Second }

public enum TouchAction { Down, Move, Up }

public static class PrinterBackends
{
    public static IPrinterBackend Create(AppConfig config) =>
        config.IsBambu
            ? new BambuBackend(config.BambuIp, config.BambuSerial, config.BambuAccessCode)
            : new MoonrakerBackend(config.MoonrakerUrl, config.MoonrakerApiKey,
                                   webcamName: config.WebcamName, secondViewName: config.SecondViewName);

    /// <summary>The printer's cameras, for the settings window. Throws if it can't be reached.</summary>
    public static async Task<IReadOnlyList<WebcamInfo>> ListWebcamsAsync(AppConfig config, CancellationToken ct = default)
    {
        if (config.IsBambu)
            return []; // one built-in camera, nothing to choose
        using var backend = new MoonrakerBackend(config.MoonrakerUrl, config.MoonrakerApiKey);
        return await backend.ListWebcamsAsync(ct);
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
