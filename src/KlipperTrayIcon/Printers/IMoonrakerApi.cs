using System;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace KlipperTrayIcon.Printers;

/// <summary>
/// Moonraker's HTTP API (https://moonraker.readthedocs.io/en/latest/web_api/), for modules: the
/// printer's address and API key are already applied. Calls throw if the printer can't be reached
/// or says no (<see cref="IPrinterBackend.DescribeError"/> turns that into a message).
/// </summary>
public interface IMoonrakerApi
{
    /// <summary>The printer's address, e.g. "http://voron.local".</summary>
    string BaseUrl { get; }
    /// <summary>A GET, e.g. "/server/job_queue/status", parsed. The default timeout is 5 seconds.</summary>
    Task<JsonDocument> GetJsonAsync(string pathAndQuery, CancellationToken ct, TimeSpan? timeout = null);
    /// <summary>A request that changes something; json (if any) is sent as the body.</summary>
    Task SendAsync(HttpMethod method, string pathAndQuery, object? json, CancellationToken ct);
    /// <summary>A PNG or JPEG (e.g. a thumbnail), or null if it's missing or not an image.</summary>
    Task<byte[]?> GetImageAsync(string pathAndQuery, CancellationToken ct, TimeSpan? timeout = null);
}
