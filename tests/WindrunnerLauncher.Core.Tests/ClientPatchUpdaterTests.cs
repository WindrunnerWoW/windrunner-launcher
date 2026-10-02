using System.Net;
using System.Text;
using System.Text.Json;
using WindrunnerLauncher.Core.Client;
using WindrunnerLauncher.Core.Downloads;
using WindrunnerLauncher.Core.Models;
using WindrunnerLauncher.Core.Mods;
using WindrunnerLauncher.Core.Persistence;
using WindrunnerLauncher.Core.Security;

namespace WindrunnerLauncher.Core.Tests;

public class ClientPatchUpdaterTests
{
    private static GitHubRelease Release(params (string Name, string Content)[] assets) => new()
    {
        TagName = ClientPatchUpdater.ReleaseTag,
        Assets = assets.Select(a => new GitHubReleaseAsset
        {
            Name = a.Name,
            BrowserDownloadUrl = $"https://example.invalid/{a.Name}",
            Size = Encoding.UTF8.GetByteCount(a.Content),
            Sha256 = Checksums.Sha256Hex(Encoding.UTF8.GetBytes(a.Content))
        }).ToList()
    };

    private static string Full(string path) => Path.GetFullPath(path);

    [Fact]
    public void IsWanted_OnlyExplicitDisableSkipsPatch()
    {
        var realm = new RealmEntry();
        Assert.True(ClientPatchUpdater.IsWanted(realm, "patch-A.mpq"));

        realm.MpqFileState["patch-A.mpq"] = true;
        Assert.True(ClientPatchUpdater.IsWanted(realm, "PATCH-A.MPQ"));

        realm.MpqFileState["patch-A.mpq"] = false;
        Assert.False(ClientPatchUpdater.IsWanted(realm, "patch-A.mpq"));
    }

    [Fact]
    public void Plan_ReportsStaleAndMissingPatches_ButNotCurrentOnes()
    {
        using var tmp = new TempDir();
        var client = tmp.Dir("client");
        var data = tmp.Dir("client/Data");
        File.WriteAllText(Path.Combine(data, "patch-A.mpq"), "old");
        File.WriteAllText(Path.Combine(data, "patch-B.mpq"), "same");

        var plan = ClientPatchUpdater.Plan(client,
            Release(("patch-A.mpq", "new"), ("patch-B.mpq", "same"), ("patch-C.mpq", "fresh")));

        Assert.Equal(["patch-A.mpq", "patch-C.mpq"], plan.Select(p => p.Asset.Name));
        Assert.False(plan[0].Missing);
        Assert.Equal(Full(Path.Combine(data, "patch-A.mpq")), Full(plan[0].TargetPath));
        Assert.True(plan[1].Missing);
        Assert.Equal(Full(Path.Combine(data, "patch-C.mpq")), Full(plan[1].TargetPath));
    }

    [Fact]
    public void Plan_TargetsParkedCopyInPlace()
    {
        using var tmp = new TempDir();
        var parked = tmp.Dir($"client/Data/{ModManager.DisabledMpqFolderName}");
        File.WriteAllText(Path.Combine(parked, "patch-A.mpq"), "old");

        var plan = ClientPatchUpdater.Plan(tmp.Dir("client"), Release(("patch-A.mpq", "new")));

        Assert.Equal(Full(Path.Combine(parked, "patch-A.mpq")), Full(Assert.Single(plan).TargetPath));
    }

    [Fact]
    public void Plan_IgnoresNonMpqAndBaseArchives()
    {
        using var tmp = new TempDir();
        tmp.Dir("client/Data");

        var plan = ClientPatchUpdater.Plan(tmp.Dir("client"),
            Release(("readme.txt", "x"), ("patch-5.mpq", "x"), ("../evil.mpq", "x")));

        Assert.Empty(plan);
    }

    [Fact]
    public async Task CheckThenApply_OnlyForRealmsThatWantThePatch()
    {
        using var tmp = new TempDir();
        var paths = tmp.Paths();
        paths.EnsureLayout();
        tmp.File("client/WoW.exe", "exe");
        var live = tmp.File("client/Data/patch-A.mpq", "old");
        var state = new StateStore(paths);
        state.Settings.ClientPath = paths.Client;
        var client = new ClientManager(paths, state);

        using var assetServer = new LoopbackHttpServer(async (ctx, ct) =>
        {
            var body = Encoding.UTF8.GetBytes("new");
            ctx.Response.ContentLength64 = body.Length;
            await ctx.Response.OutputStream.WriteAsync(body, ct);
            ctx.Response.Close();
        });
        var releaseJson = JsonSerializer.Serialize(new
        {
            tag_name = "Client",
            assets = new[]
            {
                new
                {
                    name = "patch-A.mpq",
                    browser_download_url = assetServer.Url("patch-A.mpq"),
                    size = 3,
                    digest = "sha256:" + Checksums.Sha256Hex(Encoding.UTF8.GetBytes("new"))
                }
            }
        });
        using var api = new HttpClient(new StubHandler(releaseJson));
        using var downloads = new DownloadManager();
        var updater = new ClientPatchUpdater(paths, client, downloads, new GitHubReleases(api));

        var off = new RealmEntry { Id = "off" };
        off.MpqFileState["patch-A.mpq"] = false;
        var on = new RealmEntry { Id = "on" };

        await updater.CheckAsync(off);
        Assert.Empty(updater.PendingFor(off));
        Assert.Empty(await updater.ApplyAsync(off));
        Assert.Equal("old", File.ReadAllText(live));

        Assert.Equal("patch-A.mpq", Assert.Single(updater.PendingFor(on)).Asset.Name);
        Assert.Equal("old", File.ReadAllText(live)); // checking alone never downloads

        await updater.ApplyAsync(on);
        Assert.Equal("new", File.ReadAllText(live));
        Assert.Empty(updater.PendingFor(on));

        await updater.CheckAsync(on, fetch: false);
        Assert.Empty(updater.PendingFor(on));
    }

    private sealed class StubHandler(string json) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json")
            });
    }
}
