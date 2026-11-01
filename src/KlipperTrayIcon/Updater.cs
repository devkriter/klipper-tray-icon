using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace KlipperTrayIcon;

/// <summary>A newer release on GitHub.</summary>
public sealed record Release(Version Version, string Tag, string PageUrl, string ZipUrl, string? Sha256);

/// <summary>
/// Looks for new releases on GitHub (quietly: nothing pops up, the tray menu and Settings
/// just offer the update) and installs one when asked: the zip's files replace the app's,
/// the running ones renamed out of the way first, which Windows allows.
/// </summary>
public sealed class Updater : IDisposable
{
    public const string Repo = "devkriter/klipper-tray-icon";
    public const string ReleasesPage = $"https://github.com/{Repo}/releases";
    private const string LatestUrl = $"https://api.github.com/repos/{Repo}/releases/latest";
    private static readonly TimeSpan CheckEvery = TimeSpan.FromHours(24);
    private static readonly TimeSpan FirstCheckAfter = TimeSpan.FromSeconds(20);
    // Files the update leaves alone, whatever the zip holds.
    private static readonly HashSet<string> Kept = new(StringComparer.OrdinalIgnoreCase) { "config.json", "error.log" };
    private const string Staging = ".update";

    private readonly HttpClient _http;
    private readonly string _appDir;
    private readonly SynchronizationContext? _ui = SynchronizationContext.Current;
    private CancellationTokenSource _loop = new();

    public static Version Current { get; } = ParseVersion(
        typeof(Updater).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion) ?? new Version(0, 0);

    /// <summary>A newer release, once one has been found.</summary>
    public Release? Available { get; private set; }
    /// <summary>Download progress 0–1 while installing, else null.</summary>
    public double? Installing { get; private set; }
    public bool Checking { get; private set; }
    /// <summary>When the last check got an answer (null: none yet).</summary>
    public DateTime? LastChecked { get; private set; }
    /// <summary>Raised on the UI thread whenever any of the above changes.</summary>
    public event Action? Changed;

    public Updater(HttpClient? http = null, string? appDir = null)
    {
        _http = http ?? new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
        if (http == null)
            _http.DefaultRequestHeaders.UserAgent.ParseAdd($"KlipperTrayIcon/{Current}"); // GitHub requires one
        _appDir = appDir ?? ConfigStore.AppDir;
    }

    /// <summary>Checks now and then every day, or stops checking.</summary>
    public void SetAutoCheck(bool on)
    {
        _loop.Cancel();
        _loop = new CancellationTokenSource();
        if (on)
            _ = LoopAsync(_loop.Token);
    }

    private async Task LoopAsync(CancellationToken ct)
    {
        try
        {
            await Task.Delay(FirstCheckAfter, ct);
            while (true)
            {
                await CheckAsync(ct);
                await Task.Delay(CheckEvery, ct);
            }
        }
        catch (OperationCanceledException) { }
    }

    /// <summary>Asks GitHub for the latest release. Failures are ignored: it's asked again tomorrow.</summary>
    public async Task CheckAsync(CancellationToken ct = default)
    {
        if (Checking)
            return;
        Checking = true;
        Raise();
        try
        {
            using var response = await _http.GetAsync(LatestUrl, ct);
            if (!response.IsSuccessStatusCode)
                return;
            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
            var release = ParseRelease(doc.RootElement);
            LastChecked = DateTime.Now;
            Available = release != null && release.Version > Current ? release : null;
        }
        catch (Exception e) when (e is HttpRequestException or JsonException or TaskCanceledException && !ct.IsCancellationRequested) { }
        finally
        {
            Checking = false;
            Raise();
        }
    }

    internal static Release? ParseRelease(JsonElement json)
    {
        if (json.TryGetProperty("draft", out var draft) && draft.ValueKind == JsonValueKind.True
            || json.TryGetProperty("prerelease", out var pre) && pre.ValueKind == JsonValueKind.True)
            return null;
        string tag = json.TryGetProperty("tag_name", out var t) ? t.GetString() ?? "" : "";
        if (ParseVersion(tag) is not Version version)
            return null;
        string page = json.TryGetProperty("html_url", out var h) ? h.GetString() ?? ReleasesPage : ReleasesPage;
        if (!json.TryGetProperty("assets", out var assets) || assets.ValueKind != JsonValueKind.Array)
            return null;
        foreach (var asset in assets.EnumerateArray())
        {
            string name = asset.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
            if (!name.StartsWith("KlipperTrayIcon", StringComparison.OrdinalIgnoreCase)
                || !name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)
                || !asset.TryGetProperty("browser_download_url", out var url) || url.GetString() is not string zip)
                continue;
            // GitHub lists each asset's SHA-256 ("sha256:…").
            string? digest = asset.TryGetProperty("digest", out var d) ? d.GetString() : null;
            string? sha = digest?.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase) == true ? digest[7..] : null;
            return new Release(version, tag, page, zip, sha);
        }
        return null;
    }

    /// <summary>"v2.10.0", "2.10.0+79595f8" → 2.10.0.</summary>
    internal static Version? ParseVersion(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return null;
        text = text.Trim().TrimStart('v', 'V');
        int end = text.IndexOfAny(['+', '-', ' ']);
        if (end >= 0)
            text = text[..end];
        if (!Version.TryParse(text, out var v))
            return null;
        // 2.10 and 2.10.0 are the same release.
        return new Version(v.Major, v.Minor, Math.Max(v.Build, 0));
    }

    /// <summary>
    /// Downloads the release and puts its files in place of the app's. Returns the exe to start;
    /// the caller then exits. On failure the app is left as it was.
    /// </summary>
    public async Task<string> InstallAsync(Release release, CancellationToken ct = default)
    {
        if (Installing != null)
            throw new InvalidOperationException("Already updating.");
        Installing = 0;
        Raise();
        string staging = Path.Combine(_appDir, Staging);
        try
        {
            // Next to the app, so the files only move (same drive), and a folder we can't write to shows up now.
            if (Directory.Exists(staging))
                Directory.Delete(staging, true);
            Directory.CreateDirectory(staging);
            string zip = Path.Combine(staging, "update.zip");
            await DownloadAsync(release.ZipUrl, zip, ct);
            if (release.Sha256 != null)
            {
                await using var file = File.OpenRead(zip);
                string hash = Convert.ToHexString(await SHA256.HashDataAsync(file, ct));
                if (!hash.Equals(release.Sha256, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("The download was damaged (its checksum doesn't match).");
            }
            string files = Path.Combine(staging, "files");
            ZipFile.ExtractToDirectory(zip, files);
            // The zip holds a KlipperTrayIcon folder.
            string root = Directory.GetFiles(files, "KlipperTrayIcon.exe", SearchOption.AllDirectories)
                              .Select(Path.GetDirectoryName).FirstOrDefault()
                          ?? throw new InvalidDataException("The download doesn't contain KlipperTrayIcon.exe.");
            Swap(root, _appDir);
            return Path.Combine(_appDir, "KlipperTrayIcon.exe");
        }
        finally
        {
            try { if (Directory.Exists(staging)) Directory.Delete(staging, true); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
            Installing = null;
            Raise();
        }
    }

    private async Task DownloadAsync(string url, string path, CancellationToken ct)
    {
        using var response = await _http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();
        long? total = response.Content.Headers.ContentLength;
        await using var source = await response.Content.ReadAsStreamAsync(ct);
        await using var target = File.Create(path);
        var buffer = new byte[81920];
        long done = 0;
        double shown = 0;
        int read;
        while ((read = await source.ReadAsync(buffer, ct)) > 0)
        {
            await target.WriteAsync(buffer.AsMemory(0, read), ct);
            done += read;
            if (total > 0 && (double)done / total.Value - shown >= 0.01)
            {
                Installing = shown = (double)done / total.Value;
                Raise();
            }
        }
    }

    /// <summary>
    /// Moves the new files into the app folder. A running exe or loaded DLL can't be overwritten
    /// but can be renamed, so each old file becomes "name.old" (deleted on the next start).
    /// All or nothing: on any failure the old files are put back.
    /// </summary>
    internal static void Swap(string newDir, string appDir)
    {
        var done = new List<(string Target, string? Old)>();
        try
        {
            foreach (string file in Directory.GetFiles(newDir))
            {
                string name = Path.GetFileName(file);
                if (Kept.Contains(name))
                    continue;
                string target = Path.Combine(appDir, name);
                string? old = null;
                if (File.Exists(target))
                {
                    old = target + ".old";
                    if (File.Exists(old))
                        File.Delete(old);
                    File.Move(target, old);
                }
                done.Add((target, old));
                File.Move(file, target);
            }
        }
        catch
        {
            foreach (var (target, old) in Enumerable.Reverse(done))
            {
                try
                {
                    if (old == null || File.Exists(old))
                        File.Delete(target);
                    if (old != null)
                        File.Move(old, target);
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
            }
            throw;
        }
    }

    /// <summary>Deletes what the last update left behind (at startup, once the old version has exited).</summary>
    public static void CleanUp(string? appDir = null)
    {
        appDir ??= ConfigStore.AppDir;
        try
        {
            foreach (string old in Directory.GetFiles(appDir, "*.old"))
                try { File.Delete(old); }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
            string staging = Path.Combine(appDir, Staging);
            if (Directory.Exists(staging))
                Directory.Delete(staging, true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }

    public static void OpenPage(string url) => Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });

    private void Raise()
    {
        if (_ui != null && SynchronizationContext.Current != _ui)
            _ui.Post(_ => Changed?.Invoke(), null);
        else
            Changed?.Invoke();
    }

    public void Dispose()
    {
        _loop.Cancel();
        _http.Dispose();
    }
}
