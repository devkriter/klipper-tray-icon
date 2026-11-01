using System.IO;
using System.IO.Compression;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Xunit;

namespace KlipperTrayIcon.Tests;

/// <summary>Finding and installing new releases.</summary>
public class UpdaterTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "KlipperTrayIconTests-" + Guid.NewGuid().ToString("N"));

    public UpdaterTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); }
        catch (IOException) { }
    }

    [Theory]
    [InlineData("v2.10.0", "2.10.0")]
    [InlineData("2.10.0+79595f8", "2.10.0")]
    [InlineData("v2.11", "2.11.0")]
    [InlineData("v3.0.0-beta", "3.0.0")]
    [InlineData("latest", null)]
    [InlineData("", null)]
    public void ReadsVersions(string text, string? expected) =>
        Assert.Equal(expected, Updater.ParseVersion(text)?.ToString());

    [Fact]
    public void NewerVersionsCompareByNumberNotText() =>
        Assert.True(Updater.ParseVersion("v2.10.0") > Updater.ParseVersion("v2.9.2"));

    private static string ReleaseJson(string tag, bool prerelease = false, string? digest = null) => $$"""
        {"tag_name":"{{tag}}","prerelease":{{(prerelease ? "true" : "false")}},"draft":false,
         "html_url":"https://github.com/devkriter/klipper-tray-icon/releases/tag/{{tag}}",
         "assets":[{"name":"notes.txt","browser_download_url":"https://x/notes.txt"},
                   {"name":"KlipperTrayIcon_{{tag}}.zip","browser_download_url":"https://x/app.zip"
                    {{(digest == null ? "" : $",\"digest\":\"sha256:{digest}\"")}}}]}
        """;

    [Fact]
    public void FindsTheZipInARelease()
    {
        using var doc = JsonDocument.Parse(ReleaseJson("v2.11.0", digest: "ABC123"));
        var release = Updater.ParseRelease(doc.RootElement)!;
        Assert.Equal(new Version(2, 11, 0), release.Version);
        Assert.Equal("v2.11.0", release.Tag);
        Assert.Equal("https://x/app.zip", release.ZipUrl);
        Assert.Equal("ABC123", release.Sha256);
        Assert.EndsWith("/tag/v2.11.0", release.PageUrl);
    }

    [Fact]
    public void PrereleasesAreNotOffered()
    {
        using var doc = JsonDocument.Parse(ReleaseJson("v9.0.0", prerelease: true));
        Assert.Null(Updater.ParseRelease(doc.RootElement));
    }

    [Fact]
    public void ReleaseWithoutAZipIsNotOffered()
    {
        using var doc = JsonDocument.Parse("""{"tag_name":"v9.0.0","assets":[{"name":"src.tar.gz","browser_download_url":"u"}]}""");
        Assert.Null(Updater.ParseRelease(doc.RootElement));
    }

    private sealed class FakeGitHub(Func<string, HttpResponseMessage> answer) : HttpMessageHandler
    {
        public readonly List<string> Requests = new();

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Requests.Add(request.RequestUri!.ToString());
            return Task.FromResult(answer(request.RequestUri.ToString()));
        }
    }

    private static HttpResponseMessage Json(string json) =>
        new(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    [Fact]
    public async Task OffersOnlyANewerRelease()
    {
        using var newer = new Updater(new HttpClient(new FakeGitHub(_ => Json(ReleaseJson("v999.0.0")))), _dir);
        await newer.CheckAsync();
        Assert.Equal("v999.0.0", newer.Available?.Tag);

        using var same = new Updater(new HttpClient(new FakeGitHub(_ => Json(ReleaseJson("v" + Updater.Current)))), _dir);
        await same.CheckAsync();
        Assert.Null(same.Available);
        Assert.NotNull(same.LastChecked);
    }

    [Fact]
    public async Task FailedCheckIsQuiet()
    {
        using var updater = new Updater(new HttpClient(new FakeGitHub(_ => new HttpResponseMessage(HttpStatusCode.Forbidden))), _dir);
        await updater.CheckAsync(); // rate limited, offline...: no exception, nothing offered
        Assert.Null(updater.Available);
        Assert.False(updater.Checking);
    }

    private string MakeApp(params (string Name, string Text)[] files)
    {
        string app = Path.Combine(_dir, "app");
        Directory.CreateDirectory(app);
        foreach (var (name, text) in files)
            File.WriteAllText(Path.Combine(app, name), text);
        return app;
    }

    private static byte[] MakeZip(params (string Name, string Text)[] files)
    {
        using var stream = new MemoryStream();
        using (var zip = new ZipArchive(stream, ZipArchiveMode.Create, true))
            foreach (var (name, text) in files)
                using (var entry = new StreamWriter(zip.CreateEntry("KlipperTrayIcon/" + name).Open()))
                    entry.Write(text);
        return stream.ToArray();
    }

    [Fact]
    public async Task InstallReplacesTheAppAndKeepsTheSettings()
    {
        string app = MakeApp(("KlipperTrayIcon.exe", "old exe"), ("wpfgfx_cor3.dll", "old dll"), ("config.json", "mine"));
        byte[] zip = MakeZip(("KlipperTrayIcon.exe", "new exe"), ("wpfgfx_cor3.dll", "new dll"), ("LICENSE.txt", "gpl"),
                             ("config.json", "theirs"));
        string sha = Convert.ToHexString(SHA256.HashData(zip));
        var github = new FakeGitHub(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(zip) });
        using var updater = new Updater(new HttpClient(github), app);

        string exe = await updater.InstallAsync(new Release(new Version(9, 0, 0), "v9.0.0", "page", "https://x/app.zip", sha));

        Assert.Equal(Path.Combine(app, "KlipperTrayIcon.exe"), exe);
        Assert.Equal("new exe", File.ReadAllText(exe));
        Assert.Equal("new dll", File.ReadAllText(Path.Combine(app, "wpfgfx_cor3.dll")));
        Assert.Equal("gpl", File.ReadAllText(Path.Combine(app, "LICENSE.txt")));
        Assert.Equal("mine", File.ReadAllText(Path.Combine(app, "config.json")));
        Assert.Equal("old exe", File.ReadAllText(exe + ".old")); // the running one, moved aside
        Assert.False(Directory.Exists(Path.Combine(app, ".update")));
        Assert.Null(updater.Installing);

        Updater.CleanUp(app); // next start
        Assert.Empty(Directory.GetFiles(app, "*.old"));
    }

    [Fact]
    public async Task DamagedDownloadChangesNothing()
    {
        string app = MakeApp(("KlipperTrayIcon.exe", "old exe"));
        byte[] zip = MakeZip(("KlipperTrayIcon.exe", "new exe"));
        var github = new FakeGitHub(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(zip) });
        using var updater = new Updater(new HttpClient(github), app);

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            updater.InstallAsync(new Release(new Version(9, 0, 0), "v9.0.0", "page", "https://x/app.zip", "00")));
        Assert.Equal("old exe", File.ReadAllText(Path.Combine(app, "KlipperTrayIcon.exe")));
        Assert.Equal(["KlipperTrayIcon.exe"], Directory.GetFileSystemEntries(app).Select(Path.GetFileName));
    }

    [Fact]
    public void FailedSwapPutsTheOldFilesBack()
    {
        string app = MakeApp(("a.dll", "old a"), ("b.dll", "old b"));
        string update = Path.Combine(_dir, "new");
        Directory.CreateDirectory(update);
        File.WriteAllText(Path.Combine(update, "a.dll"), "new a");
        File.WriteAllText(Path.Combine(update, "b.dll"), "new b");
        // b.dll.old can't be replaced: the swap fails halfway.
        Directory.CreateDirectory(Path.Combine(app, "b.dll.old"));

        Assert.ThrowsAny<IOException>(() => Updater.Swap(update, app));
        Assert.Equal("old a", File.ReadAllText(Path.Combine(app, "a.dll")));
        Assert.Equal("old b", File.ReadAllText(Path.Combine(app, "b.dll")));
        Assert.False(File.Exists(Path.Combine(app, "a.dll.old")));
    }
}
