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

    void Start();
    Task<PrinterStatus> GetStatusAsync(CancellationToken ct);
    /// <summary>For the settings window's Test button.</summary>
    Task<(bool Ok, string Message)> TestConnectionAsync(CancellationToken ct);

    /// <summary>Frames of the live camera until cancelled or the stream ends. The caller disposes each one.</summary>
    IAsyncEnumerable<CameraFrame> GetWebcamFramesAsync(CancellationToken ct);
    /// <summary>One JPEG if the printer can serve one cheaply, else null.</summary>
    Task<byte[]?> GetWebcamSnapshotAsync(TimeSpan timeout, CancellationToken ct);

    /// <summary>Recent console lines, oldest first. Throws if the printer can't be reached.</summary>
    Task<IReadOnlyList<ConsoleEntry>> GetConsoleMessagesAsync(CancellationToken ct);
    /// <summary>Human-readable reason for an exception thrown by this backend.</summary>
    string DescribeError(Exception e);
}

public static class PrinterBackends
{
    public static IPrinterBackend Create(AppConfig config) =>
        config.IsBambu
            ? new BambuBackend(config.BambuIp, config.BambuSerial, config.BambuAccessCode)
            : new MoonrakerBackend(config.MoonrakerUrl, config.MoonrakerApiKey);

    /// <summary>Spins up a throwaway backend for config and reports (ok, message).</summary>
    public static async Task<(bool Ok, string Message)> TestConnectionAsync(AppConfig config, CancellationToken ct = default)
    {
        using var backend = Create(config);
        backend.Start();
        try { return await backend.TestConnectionAsync(ct); }
        catch (Exception e) { return (false, backend.DescribeError(e)); }
    }
}
