using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using KlipperTrayIcon.Printers;

namespace KlipperTrayIcon.Modules.PrintQueue;

/// <summary>State is Moonraker's queue_state: ready, loading, starting or paused.</summary>
public sealed record QueueStatus(string State, IReadOnlyList<QueuedJob> Jobs);

public sealed record QueuedJob(string JobId, string Path);

/// <summary>A file in the printer's G-code folder; Modified is Unix time in seconds.</summary>
public sealed record GcodeFile(string Path, double Modified);

/// <summary>Thumbnail (for the list) and Preview (the biggest, for hovering) are PNG or JPEG bytes.</summary>
public sealed record GcodeDetails(double? EstimatedSeconds, byte[]? Thumbnail, byte[]? Preview = null);

/// <summary>
/// Moonraker's job queue (what Mainsail's queue panel shows) and its G-code files.
/// Every call throws if the printer can't be reached.
/// </summary>
public sealed class PrintQueueApi(IMoonrakerApi printer)
{
    private readonly Dictionary<string, GcodeDetails> _details = new();
    // When each file's cached details were for (its modified time).
    private readonly Dictionary<string, double> _detailsModified = new();

    /// <summary>Whether this Moonraker has the job queue (it's an optional component).</summary>
    public async Task<bool> HasJobQueueAsync(CancellationToken ct)
    {
        using var doc = await printer.GetJsonAsync("/server/info", ct, TimeSpan.FromSeconds(3));
        var components = doc.RootElement.Prop("result").Prop("components");
        return components.ValueKind == JsonValueKind.Array
               && components.EnumerateArray().Any(c => c.GetString() == "job_queue");
    }

    public async Task<QueueStatus> GetQueueAsync(CancellationToken ct)
    {
        using var doc = await printer.GetJsonAsync("/server/job_queue/status", ct, TimeSpan.FromSeconds(3));
        var result = doc.RootElement.Prop("result");
        var jobs = result.Prop("queued_jobs");
        return new QueueStatus(result.Str("queue_state", "paused"),
            jobs.ValueKind != JsonValueKind.Array ? []
                : jobs.EnumerateArray().Select(j => new QueuedJob(j.Str("job_id"), j.Str("filename"))).ToList());
    }

    /// <summary>The most recently changed G-code files, newest first.</summary>
    public async Task<IReadOnlyList<GcodeFile>> GetRecentFilesAsync(int count, CancellationToken ct)
    {
        using var doc = await printer.GetJsonAsync("/server/files/list?root=gcodes", ct);
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

    /// <summary>The slicer's time estimate and a small thumbnail, if the file has them. Cached.</summary>
    public async Task<GcodeDetails> GetFileDetailsAsync(string path, CancellationToken ct)
    {
        lock (_details)
            if (_details.TryGetValue(path, out var cached))
                return cached;
        using var doc = await printer.GetJsonAsync("/server/files/metadata?filename=" + Uri.EscapeDataString(path),
                                                   ct, TimeSpan.FromSeconds(3));
        var meta = doc.RootElement.Prop("result");
        // Thumbnails sit next to the file (e.g. ".thumbs/name-48x48.png").
        string folder = path.Contains('/') ? path[..(path.LastIndexOf('/') + 1)] : "";
        async Task<byte[]?> Fetch(string? relative) => relative == null ? null
            : await printer.GetImageAsync("/server/files/gcodes/" +
                                          string.Join("/", (folder + relative).Split('/').Select(Uri.EscapeDataString)),
                                          ct, TimeSpan.FromSeconds(3));
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
        printer.SendAsync(HttpMethod.Post, "/server/job_queue/job", new { filenames = new[] { path } }, ct);

    public Task RemoveFromQueueAsync(string jobId, CancellationToken ct) =>
        printer.SendAsync(HttpMethod.Delete, "/server/job_queue/job?job_ids=" + Uri.EscapeDataString(jobId), null, ct);

    /// <summary>Puts the queue in this order (Moonraker can only move a job to the front, so it's rewritten).</summary>
    public Task ReorderQueueAsync(IReadOnlyList<string> paths, CancellationToken ct) =>
        printer.SendAsync(HttpMethod.Post, "/server/job_queue/job", new { filenames = paths, reset = true }, ct);

    /// <summary>Starts the queue: if the printer is ready, the next job starts printing.</summary>
    public Task StartQueueAsync(CancellationToken ct) =>
        printer.SendAsync(HttpMethod.Post, "/server/job_queue/start", null, ct);

    /// <summary>Prints this file now (the printer must be idle).</summary>
    public Task StartPrintAsync(string path, CancellationToken ct) =>
        printer.SendAsync(HttpMethod.Post, "/printer/print/start?filename=" + Uri.EscapeDataString(path), null, ct);
}
