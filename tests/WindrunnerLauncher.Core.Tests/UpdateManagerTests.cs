using WindrunnerLauncher.Core.Downloads;
using WindrunnerLauncher.Core.Persistence;
using WindrunnerLauncher.Core.Security;
using WindrunnerLauncher.Core.Updates;

namespace WindrunnerLauncher.Core.Tests;

public class UpdateManagerTests
{
    /// <summary>
    /// A release client that cannot reach GitHub. CheckAsync would otherwise download the live
    /// launcher manifest and overwrite the test's cached one, so these tests run offline.
    /// </summary>
    private static GitHubReleases OfflineReleases() => new(new HttpClient(new OfflineHandler()));

    private sealed class OfflineHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            Task.FromException<HttpResponseMessage>(new HttpRequestException("offline"));
    }

    [Theory]
    [InlineData("1.0.1", "1.0.0", true)]
    [InlineData("v1.0.1", "1.0.0", true)]
    [InlineData("1.0.0", "1.0.0", false)]
    [InlineData("0.9.9", "1.0.0", false)]
    [InlineData("1.0.0", "v1.0.0", false)]
    [InlineData("2.0.0-beta", "1.9.9", true)]
    [InlineData("1.0.0+build5", "1.0.0", false)]
    [InlineData("", "1.0.0", false)]
    [InlineData("1.0.0", "", true)]
    [InlineData("nightly-b", "nightly-a", true)]
    [InlineData("nightly-a", "nightly-a", false)]
    public void IsNewer(string candidate, string current, bool expected)
    {
        Assert.Equal(expected, UpdateManager.IsNewer(candidate, current));
    }

    [Fact]
    public void FreshRuntime_NoUpdatesPending()
    {
        using var tmp = new TempDir();
        var paths = tmp.Paths();
        paths.EnsureLayout();
        ManifestTestData.SaveDefaultClientCatalog(paths);
        using var runtime = new LauncherRuntime(paths);
        var updates = runtime.Updates;

        Assert.False(updates.ClientUpdateRequired);
        Assert.False(updates.ServerUpdateAvailable);
        Assert.False(updates.LauncherUpdateAvailable);
        Assert.Null(updates.LastCheckUtc);
        Assert.False(updates.HasServerRollback);
        Assert.Matches(@"^\d+\.\d+\.\d+$", updates.CurrentLauncherVersion);
        Assert.Contains("Client content", updates.PatchNotes);
    }

    [Fact]
    public void IgnoreServerVersion_Persists_AndClears()
    {
        using var tmp = new TempDir();
        var paths = tmp.Paths();
        using var runtime = new LauncherRuntime(paths);
        var changed = 0;
        runtime.Updates.Changed += () => changed++;

        runtime.Updates.IgnoreServerVersion("v0.9.0");
        Assert.Equal("v0.9.0", runtime.State.Settings.IgnoredServerRelease);
        Assert.False(runtime.Updates.ServerUpdateAvailable);
        Assert.Equal("v0.9.0", new StateStore(paths).Settings.IgnoredServerRelease);
        Assert.Equal(1, changed);

        runtime.Updates.ClearIgnoredServerVersion();
        Assert.Null(runtime.State.Settings.IgnoredServerRelease);
        Assert.Null(new StateStore(paths).Settings.IgnoredServerRelease);
        Assert.Equal(2, changed);
    }

    [Fact]
    public void IgnoreServerVersion_WithNothingKnown_IsNoOp()
    {
        using var tmp = new TempDir();
        using var runtime = new LauncherRuntime(tmp.Paths());
        runtime.Updates.IgnoreServerVersion();
        Assert.Null(runtime.State.Settings.IgnoredServerRelease);
    }

    [Fact]
    public async Task UpdateClientAsync_NothingToDo_ReportsUpToDate()
    {
        using var tmp = new TempDir();
        using var runtime = new LauncherRuntime(tmp.Paths());
        var log = await runtime.Updates.UpdateClientAsync();
        Assert.Single(log);
        Assert.Contains("up to date", log[0]);
    }

    [Fact]
    public async Task UpdateLauncherAsync_WithoutAnnouncement_Throws()
    {
        using var tmp = new TempDir();
        using var runtime = new LauncherRuntime(tmp.Paths());
        await Assert.ThrowsAsync<InvalidOperationException>(() => runtime.Updates.UpdateLauncherAsync());
    }

    [Fact]
    public async Task RollbackServerAsync_WithoutBackup_Throws()
    {
        using var tmp = new TempDir();
        using var runtime = new LauncherRuntime(tmp.Paths());
        await Assert.ThrowsAsync<InvalidOperationException>(() => runtime.Updates.RollbackServerAsync());
    }

    [Fact]
    public async Task UpdateServerAsync_WithoutKnownRelease_Throws()
    {
        using var tmp = new TempDir();
        using var runtime = new LauncherRuntime(tmp.Paths());
        await Assert.ThrowsAsync<InvalidOperationException>(() => runtime.Updates.UpdateServerAsync());
    }

    [Fact]
    public async Task CheckAsync_IgnoresUnsignedLauncherManifest_EvenWithChecksum()
    {
        using var tmp = new TempDir();
        var paths = tmp.Paths();
        paths.EnsureLayout();
        File.WriteAllText(Path.Combine(paths.ManifestCache, "launcher.json"), """
            {
              "schema": 1,
              "version": "99.0.0",
              "exeUrl": "https://example.invalid/launcher.exe",
              "sha256": "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
              "size": 12
            }
            """);

        using var runtime = new LauncherRuntime(paths, releases: OfflineReleases());
        var result = await runtime.Updates.CheckAsync();

        Assert.False(runtime.Updates.LauncherUpdateAvailable);
        Assert.Null(runtime.Updates.LatestLauncher);
        Assert.DoesNotContain(result.Problems, p => p.Contains("unsigned", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task CheckAsync_AcceptsSignedLauncherEnvelope()
    {
        using var tmp = new TempDir();
        var paths = tmp.Paths();
        paths.EnsureLayout();

        var payload = """{"schema":1,"version":"99.0.0","exeUrl":"https://example.invalid/launcher.exe","sha256":"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa","size":12,"linuxUrl":"https://example.invalid/launcher","linuxSha256":"bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb","linuxSize":12}""";
        var message = Encoding.UTF8.GetBytes(payload);
        var signature = Ed25519Signer.Sign(Convert.FromBase64String(DevSigningKeys.LauncherPrivateB64), message);
        var envelope = new SignedManifestEnvelope
        {
            KeyId = "launcher-dev-1",
            Algorithm = "Ed25519",
            SignatureB64 = Convert.ToBase64String(signature),
            PayloadJson = payload
        };
        JsonStore.Save(Path.Combine(paths.ManifestCache, UpdateManager.LauncherEnvelopeFileName), envelope);

        using var runtime = new LauncherRuntime(paths, releases: OfflineReleases());
        await runtime.Updates.CheckAsync();

        Assert.True(runtime.Updates.LauncherUpdateAvailable);
        Assert.Equal("99.0.0", runtime.Updates.LatestLauncher?.Version);
    }

    [Fact]
    public async Task CheckAsync_RequiresTheDownloadUrlForThisOperatingSystem()
    {
        using var tmp = new TempDir();
        var paths = tmp.Paths();
        paths.EnsureLayout();

        var payload = """{"schema":1,"version":"99.0.0","exeUrl":"https://example.invalid/launcher.exe","sha256":"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa","size":12}""";
        var message = Encoding.UTF8.GetBytes(payload);
        var signature = Ed25519Signer.Sign(Convert.FromBase64String(DevSigningKeys.LauncherPrivateB64), message);
        JsonStore.Save(Path.Combine(paths.ManifestCache, UpdateManager.LauncherEnvelopeFileName), new SignedManifestEnvelope
        {
            KeyId = "launcher-dev-1",
            Algorithm = "Ed25519",
            SignatureB64 = Convert.ToBase64String(signature),
            PayloadJson = payload
        });

        using var runtime = new LauncherRuntime(paths, releases: OfflineReleases());
        await runtime.Updates.CheckAsync();

        if (OperatingSystem.IsLinux())
            Assert.False(runtime.Updates.LauncherUpdateAvailable);
        else
            Assert.True(runtime.Updates.LauncherUpdateAvailable);
    }

    [Fact]
    public void CachedServerNotes_AreLoadedIntoPatchNotes()
    {
        using var tmp = new TempDir();
        var paths = tmp.Paths();
        paths.EnsureLayout();
        File.WriteAllText(Path.Combine(paths.ManifestCache, UpdateManager.ServerReleaseNotesFileName), "Fixed the thing.");
        JsonStore.Save(paths.SettingsFile, new LauncherSettings { InstalledServerVersion = "v0.4.0" });
        using var runtime = new LauncherRuntime(paths);

        Assert.Equal("v0.4.0", runtime.Updates.LatestServerRelease);
        Assert.Equal("Fixed the thing.", runtime.Updates.LatestServerNotes);
        Assert.Contains("Server v0.4.0", runtime.Updates.PatchNotes);
        Assert.Contains("Fixed the thing.", runtime.Updates.PatchNotes);
    }
}
