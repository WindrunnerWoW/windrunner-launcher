using System.Text;
using WindrunnerLauncher.Core.Downloads;
using WindrunnerLauncher.Core.Mods;
using WindrunnerLauncher.Core.Models;

namespace WindrunnerLauncher.Core.Tests;

public sealed class AddonManagerTests
{
    [Fact]
    public async Task InstallAllMatchingAssets_MergesComplementaryZipFiles()
    {
        using var tmp = new TempDir("addon-multi");
        var paths = tmp.Paths();
        paths.EnsureLayout();

        var first = await MakeArchiveAsync(tmp, "part1", ("VoiceOverData.toc", "## Interface: 11200\n"), ("voice-1.ogg", "one"));
        var second = await MakeArchiveAsync(tmp, "part2", ("voice-2.ogg", "two"));

        using var server = new LoopbackHttpServer((ctx, _) =>
        {
            var path = ctx.Request.Url!.AbsolutePath;
            var origin = $"{ctx.Request.Url.Scheme}://{ctx.Request.Url.Authority}";
            if (path.EndsWith("/releases/latest", StringComparison.Ordinal))
            {
                var json = $$"""
                    {
                      "tag_name": "v1.0.0",
                      "assets": [
                        { "name": "windrunner-voiceover-data-part1.zip", "browser_download_url": "{{origin}}/part1.zip", "size": {{first.Length}} },
                        { "name": "windrunner-voiceover-data-part2.zip", "browser_download_url": "{{origin}}/part2.zip", "size": {{second.Length}} }
                      ]
                    }
                    """;
                ctx.Response.ContentType = "application/json";
                return LoopbackHttpServer.WriteAsync(ctx, Encoding.UTF8.GetBytes(json));
            }

            if (path.EndsWith("/part1.zip", StringComparison.Ordinal))
                return LoopbackHttpServer.WriteAsync(ctx, first);
            if (path.EndsWith("/part2.zip", StringComparison.Ordinal))
                return LoopbackHttpServer.WriteAsync(ctx, second);

            return LoopbackHttpServer.WriteAsync(ctx, [], status: 404);
        });

        using var http = new HttpClient(new RewriteHostHandler(server.BaseUrl));
        using var downloads = new DownloadManager(http);
        var manager = new AddonManager(paths, downloads, new ReleaseCatalog(http));
        var entry = new AddonEntry
        {
            Id = "windrunner-voiceover-data",
            Name = "Windrunner Voiceover Data",
            GitHubRepo = "WindrunnerWoW/windrunner-voiceover-data",
            AssetFilter = "windrunner-voiceover-data*.zip",
            InstallAllMatchingAssets = true
        };

        await manager.InstallOrUpdateAsync(entry, paths.Client);

        var installed = Path.Combine(paths.Client, "Interface", "AddOns", "VoiceOverData");
        Assert.Equal("one", await File.ReadAllTextAsync(Path.Combine(installed, "voice-1.ogg")));
        Assert.Equal("two", await File.ReadAllTextAsync(Path.Combine(installed, "voice-2.ogg")));
        Assert.Equal("v1.0.0", entry.Version);
        Assert.True(entry.Installed);
        Assert.True(entry.Enabled);
    }

    [Theory]
    [InlineData(2)]
    [InlineData(12)]
    public async Task InstallAllMatchingAssets_JoinsNumberedZipPartsWithExistingZipFilter(int partCount)
    {
        using var tmp = new TempDir("addon-split");
        var paths = tmp.Paths();
        paths.EnsureLayout();
        var audio = string.Concat(Enumerable.Range(0, 500).Select(i => $"voice-{i};"));
        var archive = await MakeArchiveAsync(tmp, "whole",
            ("VoiceOverData.toc", "## Interface: 11200\n"), ("voice.ogg", audio));
        var parts = Enumerable.Range(0, partCount).Select(i =>
        {
            var start = archive.Length * i / partCount;
            var end = archive.Length * (i + 1) / partCount;
            return (Name: $"AI_VoiceOverData_Turtle.zip.{i + 1:000}", Payload: archive[start..end]);
        }).Reverse().ToArray(); // GitHub's asset order must not determine concatenation order.

        using var server = MakeReleaseServer(parts);
        using var http = new HttpClient(new RewriteHostHandler(server.BaseUrl));
        using var downloads = new DownloadManager(http);
        var manager = new AddonManager(paths, downloads, new ReleaseCatalog(http));
        var entry = SplitEntry();

        await manager.InstallOrUpdateAsync(entry, paths.Client);

        var installed = Path.Combine(paths.Client, "Interface", "AddOns", "VoiceOverData");
        Assert.Equal(audio, await File.ReadAllTextAsync(Path.Combine(installed, "voice.ogg")));
        Assert.True(File.Exists(Path.Combine(installed, "VoiceOverData.toc")));
        Assert.Equal(["VoiceOverData"], entry.FolderNames);
        Assert.Equal("1.0", entry.InstalledVersion);
        Assert.True(entry.Installed);
        Assert.True(entry.Enabled);
        Assert.Empty(Directory.EnumerateFileSystemEntries(Path.Combine(paths.Mods, "addons", entry.Id)));
    }

    [Theory]
    [InlineData("AI_VoiceOverData_Turtle.zip.002", "AI_VoiceOverData_Turtle.zip.003", ".001")]
    [InlineData("AI_VoiceOverData_Turtle.zip.001", "AI_VoiceOverData_Turtle.zip.003", ".002")]
    [InlineData("AI_VoiceOverData_Turtle.zip.000", "AI_VoiceOverData_Turtle.zip.001", ".001")]
    public async Task SplitZip_MissingOrInvalidPartsAreRejectedBeforeDownloading(string first, string second, string expected)
    {
        using var tmp = new TempDir("addon-missing-part");
        var paths = tmp.Paths();
        paths.EnsureLayout();
        using var server = MakeReleaseServer((first, []), (second, []));
        using var http = new HttpClient(new RewriteHostHandler(server.BaseUrl));
        using var downloads = new DownloadManager(http);
        var downloadStarted = false;
        downloads.Progress += _ => downloadStarted = true;
        var manager = new AddonManager(paths, downloads, new ReleaseCatalog(http));
        var entry = SplitEntry();

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => manager.InstallOrUpdateAsync(entry, paths.Client));

        Assert.Contains($"expected {expected}", error.Message);
        Assert.False(downloadStarted);
        Assert.False(entry.Installed);
    }

    [Fact]
    public async Task SplitZip_InvalidArchivePreservesInstalledAddon()
    {
        using var tmp = new TempDir("addon-corrupt-split");
        var paths = tmp.Paths();
        paths.EnsureLayout();
        var installed = Directory.CreateDirectory(Path.Combine(paths.Client, "Interface", "AddOns", "VoiceOverData")).FullName;
        await File.WriteAllTextAsync(Path.Combine(installed, "voice.ogg"), "old data");
        using var server = MakeReleaseServer(
            ("AI_VoiceOverData_Turtle.zip.001", [1, 2]),
            ("AI_VoiceOverData_Turtle.zip.002", [3, 4]));
        using var http = new HttpClient(new RewriteHostHandler(server.BaseUrl));
        using var downloads = new DownloadManager(http);
        var manager = new AddonManager(paths, downloads, new ReleaseCatalog(http));
        var entry = SplitEntry();
        entry.Installed = true;
        entry.InstalledVersion = "previous";
        entry.FolderNames = ["VoiceOverData"];

        await Assert.ThrowsAsync<InvalidDataException>(() => manager.InstallOrUpdateAsync(entry, paths.Client));

        Assert.Equal("old data", await File.ReadAllTextAsync(Path.Combine(installed, "voice.ogg")));
        Assert.Equal("previous", entry.InstalledVersion);
    }

    [Fact]
    public async Task RefreshLatestVersion_NotifiesChangesAndPersistsVersion()
    {
        using var tmp = new TempDir("addon-version-refresh");
        var paths = tmp.Paths();
        paths.EnsureLayout();
        using var server = MakeReleaseServer(("addon.zip", []));
        using var http = new HttpClient(new RewriteHostHandler(server.BaseUrl));
        using var downloads = new DownloadManager(http);
        var manager = new AddonManager(paths, downloads, new ReleaseCatalog(http));
        var entry = manager.Addons[0];
        entry.GitHubRepo = "owner/repo";
        entry.AssetFilter = "addon.zip";
        entry.InstallAllMatchingAssets = false;
        entry.Version = "previous";
        var changes = 0;
        manager.Changed += () => changes++;

        Assert.Equal("1.0", await manager.RefreshLatestVersionAsync(entry));
        Assert.Equal("1.0", await manager.RefreshLatestVersionAsync(entry));

        Assert.Equal(1, changes);
        var reloaded = new AddonManager(paths, downloads, new ReleaseCatalog(http));
        Assert.Equal("1.0", reloaded.Addons.Single(addon => addon.Id == entry.Id).Version);
    }

    private static AddonEntry SplitEntry() => new()
    {
        Id = "windrunner-voiceover-data",
        Name = "Windrunner Voiceover Data",
        GitHubRepo = "WindrunnerWoW/windrunner-voiceover-data",
        AssetFilter = "*.zip",
        InstallAllMatchingAssets = true
    };

    private static LoopbackHttpServer MakeReleaseServer(params (string Name, byte[] Payload)[] files) => new((ctx, _) =>
    {
        var path = ctx.Request.Url!.AbsolutePath;
        var origin = $"{ctx.Request.Url.Scheme}://{ctx.Request.Url.Authority}";
        if (path.EndsWith("/releases/latest", StringComparison.Ordinal))
        {
            var assets = string.Join(",", files.Select(file => $$"""
                { "name": "{{file.Name}}", "browser_download_url": "{{origin}}/{{file.Name}}", "size": {{file.Payload.Length}} }
                """));
            var json = $$"""{ "tag_name": "1.0", "assets": [{{assets}}] }""";
            ctx.Response.ContentType = "application/json";
            return LoopbackHttpServer.WriteAsync(ctx, Encoding.UTF8.GetBytes(json));
        }
        foreach (var file in files)
            if (path == $"/{file.Name}")
                return LoopbackHttpServer.WriteAsync(ctx, file.Payload);
        return LoopbackHttpServer.WriteAsync(ctx, [], status: 404);
    });

    private static async Task<byte[]> MakeArchiveAsync(TempDir tmp, string id, params (string Name, string Contents)[] files)
    {
        var source = tmp.Dir(id);
        var addon = Directory.CreateDirectory(Path.Combine(source, "VoiceOverData")).FullName;
        foreach (var file in files)
            await File.WriteAllTextAsync(Path.Combine(addon, file.Name), file.Contents);

        var zip = tmp.Combine($"{id}.zip");
        ArchiveUtil.ZipDirectory(source, zip);
        return await File.ReadAllBytesAsync(zip);
    }

    private sealed class RewriteHostHandler(string baseUrl) : DelegatingHandler(new HttpClientHandler())
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var original = request.RequestUri!;
            request.RequestUri = new Uri(new Uri(baseUrl), original.PathAndQuery.TrimStart('/'));
            return base.SendAsync(request, cancellationToken);
        }
    }
}
