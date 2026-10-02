using WindrunnerLauncher.Core.Downloads;
using WindrunnerLauncher.Core.Mods;
using WindrunnerLauncher.Core.Persistence;

namespace WindrunnerLauncher.Core.Tests;

public class ModReleaseTests
{
    [Fact]
    public void Select_PrefersPlainVanillaFixesOverDxvkBundle()
    {
        var release = Release(
            Asset("vanillafixes-1.5.3-dxvk.zip", "https://example.invalid/dxvk.zip"),
            Asset("vanillafixes-1.5.3.zip", "https://example.invalid/plain.zip"));

        var picked = ModReleaseSelector.Select(release, new ManagedAsset
        {
            AssetContains = "vanillafixes*.zip"
        });

        Assert.Equal("vanillafixes-1.5.3.zip", picked!.Name);
    }

    [Fact]
    public void Select_PrefersWindowsVanillaTweaks()
    {
        var release = Release(
            Asset("vanilla-tweaks_v1.6.0_x86_64-apple-darwin.zip", "https://example.invalid/mac.zip"),
            Asset("vanilla-tweaks_v1.6.0_x86_64-pc-windows-gnu.zip.sha256sum", "https://example.invalid/sum"),
            Asset("vanilla-tweaks_v1.6.0_x86_64-pc-windows-gnu.zip", "https://example.invalid/win.zip"));

        var picked = ModReleaseSelector.Select(release, new ManagedAsset
        {
            AssetContains = "vanilla-tweaks*.zip"
        });

        Assert.Equal("vanilla-tweaks_v1.6.0_x86_64-pc-windows-gnu.zip", picked!.Name);
    }

    [Fact]
    public void Select_DropsDebugUnitXpArchive()
    {
        var release = Release(
            Asset("UnitXP_SP3-debug v89.zip", "https://example.invalid/debug.zip"),
            Asset("UnitXP_SP3 v89.zip", "https://example.invalid/release.zip"));

        var picked = ModReleaseSelector.Select(release, new ManagedAsset
        {
            Destination = "UnitXP_SP3.dll",
            AssetContains = "UnitXP_SP3*.zip"
        });

        Assert.Equal("UnitXP_SP3 v89.zip", picked!.Name);
    }

    [Fact]
    public void Select_ReturnsNullWhenOnlyNonWindowsBuildsMatch()
    {
        var release = Release(Asset("vanilla-tweaks_v1.6.0_x86_64-apple-darwin.zip", "https://example.invalid/mac.zip"));
        Assert.Null(ModReleaseSelector.Select(release, new ManagedAsset { AssetContains = "vanilla-tweaks*.zip" }));
    }

    [Fact]
    public void PayloadFileName_UsesReleaseFileName_NotTheClientDestination()
    {
        var name = ModManager.PayloadFileName(
            new ManagedAsset { Id = "unitxp", Destination = "UnitXP_SP3.dll" },
            "https://codeberg.org/konaka/UnitXP_SP3/releases/download/v89/UnitXP_SP3%20v89.zip");
        Assert.Equal("UnitXP_SP3 v89.zip", name);
    }

    [Fact]
    public void TryParse_AcceptsGitHubAndCodebergSpecs()
    {
        Assert.True(ReleaseCatalog.TryParse("namreeb/nampower", out var host, out var repo));
        Assert.Equal("github.com", host);
        Assert.Equal("namreeb/nampower", repo);

        Assert.True(ReleaseCatalog.TryParse("codeberg.org/konaka/UnitXP_SP3", out host, out repo));
        Assert.Equal("codeberg.org", host);
        Assert.Equal("konaka/UnitXP_SP3", repo);

        Assert.False(ReleaseCatalog.TryParse("not a repo", out _, out _));
    }

    [Fact]
    public async Task Resolve_Codeberg_SkipsDebugArchive()
    {
        const string plain = "https://example.invalid/UnitXP_SP3-v89.zip";
        using var server = new LoopbackHttpServer((ctx, _) =>
        {
            if (!ctx.Request.Url!.AbsolutePath.Contains("/repos/konaka/UnitXP_SP3/releases", StringComparison.Ordinal))
                return LoopbackHttpServer.WriteAsync(ctx, [], status: 404);

            var json = """
                [
                  {
                    "tag_name": "v89",
                    "draft": false,
                    "prerelease": false,
                    "assets": [
                      { "name": "UnitXP_SP3-debug v89.zip", "browser_download_url": "https://example.invalid/debug.zip", "size": 1 },
                      { "name": "UnitXP_SP3 v89.zip", "browser_download_url": "https://example.invalid/UnitXP_SP3-v89.zip", "size": 2 }
                    ]
                  }
                ]
                """;
            ctx.Response.ContentType = "application/json";
            return LoopbackHttpServer.WriteAsync(ctx, Encoding.UTF8.GetBytes(json));
        });

        using var http = new HttpClient(new RewriteHostHandler(server.BaseUrl));
        var catalog = new ReleaseCatalog(http);
        var url = await catalog.ResolveAsync(new ManagedAsset
        {
            Id = "unitxp",
            DisplayName = "UnitXP SP3",
            GitHubRepo = "codeberg.org/konaka/UnitXP_SP3",
            AssetContains = "UnitXP_SP3*.zip",
            Destination = "UnitXP_SP3.dll"
        });

        Assert.Equal(plain, url);
    }

    [Fact]
    public async Task Download_ResolvesLatestRelease_ThenEnableExtractsIt()
    {
        using var tmp = new TempDir();
        var paths = tmp.Paths();
        paths.EnsureLayout();
        var src = tmp.Dir("vf");
        File.WriteAllText(Path.Combine(src, "VanillaFixes.exe"), "loader");
        var zipPath = tmp.Combine("payload.zip");
        ArchiveUtil.ZipDirectory(src, zipPath);
        var zipBytes = await File.ReadAllBytesAsync(zipPath);

        using var server = new LoopbackHttpServer((ctx, _) =>
        {
            var path = ctx.Request.Url!.AbsolutePath;
            if (path.EndsWith("/releases/latest", StringComparison.Ordinal))
            {
                var origin = $"{ctx.Request.Url.Scheme}://{ctx.Request.Url.Authority}";
                var json = $$"""
                    {
                      "tag_name": "v1.5.3",
                      "assets": [
                        { "name": "vanillafixes-1.5.3-dxvk.zip", "browser_download_url": "{{origin}}/dxvk.zip", "size": 1 },
                        { "name": "vanillafixes-1.5.3.zip", "browser_download_url": "{{origin}}/vanillafixes-1.5.3.zip", "size": 2 }
                      ]
                    }
                    """;
                ctx.Response.ContentType = "application/json";
                return LoopbackHttpServer.WriteAsync(ctx, Encoding.UTF8.GetBytes(json));
            }

            if (path.EndsWith("/vanillafixes-1.5.3.zip", StringComparison.Ordinal))
                return LoopbackHttpServer.WriteAsync(ctx, zipBytes);
            return LoopbackHttpServer.WriteAsync(ctx, [], status: 404);
        });

        using var http = new HttpClient(new RewriteHostHandler(server.BaseUrl));
        using var downloads = new DownloadManager();
        var state = new StateStore(paths);
        await File.WriteAllTextAsync(Path.Combine(paths.Client, "WoW.exe"), "MZ");
        ManifestTestData.SaveClient(paths, new ClientManifest
        {
            Version = "dl",
            Assets =
            [
                new ManagedAsset
                {
                    Id = "vanillafixes",
                    DisplayName = "VanillaFixes",
                    Kind = ModKind.ZipRoot,
                    GitHubRepo = "hannesmann/vanillafixes",
                    AssetContains = "vanillafixes*.zip"
                }
            ]
        });

        var mods = new ModManager(paths, state, downloads, new ReleaseCatalog(http));
        await mods.DownloadAssetAsync(state.SelectedRealm(), "vanillafixes");
        Assert.False(File.Exists(Path.Combine(paths.Client, "VanillaFixes.exe")));
        Assert.False(state.SelectedRealm().ManagedModState.ContainsKey("vanillafixes"));
        await mods.SetEnabledAsync(state.SelectedRealm(), "vanillafixes", true);

        Assert.Equal("loader", await File.ReadAllTextAsync(Path.Combine(paths.Client, "VanillaFixes.exe")));
        Assert.True(File.Exists(Path.Combine(paths.ModStore("vanillafixes"), "vanillafixes-1.5.3.zip")));
        Assert.False(File.Exists(Path.Combine(paths.ModStore("vanillafixes"), "vanillafixes-1.5.3-dxvk.zip")));
        Assert.True(state.SelectedRealm().ManagedModState["vanillafixes"]);
    }

    [Fact]
    public async Task Download_ReleaseLookupFails_LeavesToggleOff()
    {
        using var tmp = new TempDir();
        var paths = tmp.Paths();
        paths.EnsureLayout();
        await File.WriteAllTextAsync(Path.Combine(paths.Client, "WoW.exe"), "MZ");
        ManifestTestData.SaveClient(paths, new ClientManifest
        {
            Version = "dl",
            Assets =
            [
                new ManagedAsset
                {
                    Id = "vanillafixes",
                    DisplayName = "VanillaFixes",
                    Kind = ModKind.ZipRoot,
                    GitHubRepo = "hannesmann/vanillafixes",
                    AssetContains = "vanillafixes*.zip"
                }
            ]
        });

        using var server = new LoopbackHttpServer((ctx, _) => LoopbackHttpServer.WriteAsync(ctx, [], status: 404));
        using var http = new HttpClient(new RewriteHostHandler(server.BaseUrl));
        using var downloads = new DownloadManager();
        var state = new StateStore(paths);
        var mods = new ModManager(paths, state, downloads, new ReleaseCatalog(http));

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            mods.DownloadAssetAsync(state.SelectedRealm(), "vanillafixes"));
        Assert.Contains("hannesmann/vanillafixes", ex.Message);
        Assert.False(state.SelectedRealm().ManagedModState.TryGetValue("vanillafixes", out var on) && on);
        Assert.False(File.Exists(Path.Combine(paths.Client, "VanillaFixes.exe")));
    }

    private static GitHubRelease Release(params GitHubReleaseAsset[] assets) => new() { Assets = [.. assets] };

    private static GitHubReleaseAsset Asset(string name, string url) => new()
    {
        Name = name,
        BrowserDownloadUrl = url
    };

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
