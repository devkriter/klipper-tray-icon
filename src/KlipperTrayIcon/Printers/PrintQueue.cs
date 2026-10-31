using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace KlipperTrayIcon.Printers;

/// <summary>
/// A printer with a print queue (Moonraker's job_queue, what Mainsail's queue
/// panel shows). Every call throws if the printer can't be reached.
/// </summary>
public interface IPrintQueue
{
    Task<QueueStatus> GetQueueAsync(CancellationToken ct);
    /// <summary>The most recently changed G-code files, newest first.</summary>
    Task<IReadOnlyList<GcodeFile>> GetRecentFilesAsync(int count, CancellationToken ct);
    /// <summary>The slicer's time estimate and a small thumbnail, if the file has them. Cached.</summary>
    Task<GcodeDetails> GetFileDetailsAsync(string path, CancellationToken ct);
    Task AddToQueueAsync(string path, CancellationToken ct);
    Task RemoveFromQueueAsync(string jobId, CancellationToken ct);
    /// <summary>Puts the queue in this order (Moonraker can only move a job to the front, so it's rewritten).</summary>
    Task ReorderQueueAsync(IReadOnlyList<string> paths, CancellationToken ct);
    /// <summary>Starts the queue: if the printer is ready, the next job starts printing.</summary>
    Task StartQueueAsync(CancellationToken ct);
}

/// <summary>State is Moonraker's queue_state: ready, loading, starting or paused.</summary>
public sealed record QueueStatus(string State, IReadOnlyList<QueuedJob> Jobs);

public sealed record QueuedJob(string JobId, string Path);

/// <summary>A file in the printer's G-code folder; Modified is Unix time in seconds.</summary>
public sealed record GcodeFile(string Path, double Modified);

/// <summary>Thumbnail is PNG or JPEG bytes.</summary>
public sealed record GcodeDetails(double? EstimatedSeconds, byte[]? Thumbnail);
