using WindrunnerLauncher.Core.Client;
using WindrunnerLauncher.Core.Persistence;

namespace WindrunnerLauncher.Core.Tests;

public class LauncherRuntimeTests
{
    private static LauncherRuntime Create(TempDir tmp) => new(tmp.Paths());

    [Fact]
    public void Constructor_CreatesLayout_ExtractsServerTemplates_AndWiresSubsystems()
    {
        using var tmp = new TempDir();
        using var runtime = Create(tmp);
        var paths = runtime.Paths;

        Assert.Equal(Path.GetFullPath(tmp.Path), paths.Root);
        foreach (var dir in new[] { paths.Client, paths.ServerRoot, paths.Mods, paths.Cache, paths.Backups, paths.Config })
            Assert.True(Directory.Exists(dir), dir);

        Assert.True(File.Exists(paths.PortableEnv));
        Assert.True(File.Exists(paths.MyIniTemplate));
        Assert.Contains("MYSQL_PORT=3307", File.ReadAllText(paths.PortableEnv));

        Assert.NotNull(runtime.State);
        Assert.NotNull(runtime.Loc);
        Assert.NotNull(runtime.Downloads);
        Assert.NotNull(runtime.Realms);
        Assert.NotNull(runtime.Client);
        Assert.NotNull(runtime.Mods);
        Assert.NotNull(runtime.MariaDb);
        Assert.NotNull(runtime.Server);
        Assert.NotNull(runtime.Play);
        Assert.NotNull(runtime.Updates);
        Assert.NotNull(runtime.Rollback);
    }

    [Fact]
    public void Constructor_DoesNotOverwriteUserEditedTemplates()
    {
        using var tmp = new TempDir();
        var paths = tmp.Paths();
        tmp.File("server/portable.env", "USER=1\n");
        using var runtime = new LauncherRuntime(paths);
        Assert.Equal("USER=1\n", File.ReadAllText(paths.PortableEnv));
    }

    [Fact]
    public void LocalRealm_IsPresentAndSelected()
    {
        using var tmp = new TempDir();
        using var runtime = Create(tmp);
        Assert.Equal(RealmEntry.LocalServerId, runtime.Realms.Local.Id);
        Assert.Equal(RealmEntry.LocalServerId, runtime.State.SelectedRealm().Id);
        Assert.Equal(RealmEntry.LocalServerId, runtime.Realms.Selected.Id);
    }

    [Fact]
    public void Loc_LoadsEnglish_AndHonoursSavedLanguageFallback()
    {
        using var tmp = new TempDir();
        var paths = tmp.Paths();
        JsonStore.Save(paths.SettingsFile, new LauncherSettings { Language = "zz" });
        using var runtime = new LauncherRuntime(paths);
        Assert.Equal("Play", runtime.Loc["play"]);
        Assert.Equal("en", runtime.Loc.Language);
    }

    [Fact]
    public void ResolvePlay_Onboarding_WhenNotCompleted()
    {
        using var tmp = new TempDir();
        using var runtime = Create(tmp);
        var status = runtime.BuildHomeStatus();

        Assert.Equal(PlayActionKind.Onboarding, status.Play.Action);
        Assert.Equal("Continue", status.Play.ButtonLabel);
        Assert.True(status.Play.Enabled);
        Assert.False(status.ClientReady);
        Assert.False(status.ServerInstalled);
        Assert.Equal(RealmEntry.LocalServerId, status.Realm!.Id);
        Assert.Equal(0, status.EnabledModCount);
        Assert.False(status.ClientUpdateRequired);
    }

    [Fact]
    public void ResolvePlay_LocalRealm_NoClient_RequiresSetup()
    {
        using var tmp = new TempDir();
        using var runtime = Create(tmp);
        runtime.State.Settings.OnboardingCompleted = true;

        var status = runtime.BuildHomeStatus();
        Assert.Equal(PlayActionKind.Onboarding, status.Play.Action);
        Assert.Equal("Locate an existing Vanilla client", status.Play.ButtonLabel);
    }

    [Fact]
    public void ResolvePlay_LocalRealm_WithClient_ServerNotInstalled_IsInstallServer()
    {
        using var tmp = new TempDir();
        using var runtime = Create(tmp);
        runtime.State.Settings.OnboardingCompleted = true;
        File.WriteAllText(Path.Combine(runtime.Paths.Client, "WoW.exe"), "MZ");

        var status = runtime.BuildHomeStatus();
        Assert.Equal(PlayActionKind.InstallServer, status.Play.Action);
        Assert.Equal("Install Server", status.Play.ButtonLabel);
    }

    [Fact]
    public void ResolvePlay_RemoteRealm_NoClient_IsLocateClient()
    {
        using var tmp = new TempDir();
        using var runtime = Create(tmp);
        runtime.State.Settings.OnboardingCompleted = true;
        var remote = runtime.Realms.Add(new RealmEntry { DisplayName = "Remote", Address = "logon.example.org" });
        runtime.Realms.Select(remote.Id);

        var status = runtime.BuildHomeStatus();
        Assert.Equal(PlayActionKind.Onboarding, status.Play.Action);
        Assert.Equal("Locate an existing Vanilla client", status.Play.ButtonLabel);
        Assert.Equal("Not installed", status.Play.BlockReason);
        Assert.False(status.ClientReady);
    }

    [Fact]
    public void BuildHomeStatus_InPlaceClientPath_IsReady_WhenManagedFolderIsEmpty()
    {
        using var tmp = new TempDir();
        using var runtime = Create(tmp);
        var external = tmp.Dir("existing-install");
        File.WriteAllText(Path.Combine(external, "WoW.exe"), "MZ");
        runtime.State.Settings.OnboardingCompleted = true;
        runtime.State.Settings.ClientPath = external;
        runtime.State.Settings.ClientIsManagedCopy = false;

        var status = runtime.BuildHomeStatus();

        Assert.True(status.ClientReady);
        Assert.False(runtime.Client.IsValid(runtime.Paths.Client));
        Assert.Equal(PlayActionKind.InstallServer, status.Play.Action);
    }

    [Fact]
    public void ResolvePlay_RemoteRealm_WithClient_IsPlay()
    {
        using var tmp = new TempDir();
        using var runtime = Create(tmp);
        runtime.State.Settings.OnboardingCompleted = true;
        File.WriteAllText(Path.Combine(runtime.Paths.Client, "WoW.exe"), "MZ");
        var remote = runtime.Realms.Add(new RealmEntry { DisplayName = "Remote", Address = "logon.example.org" });
        runtime.Realms.Select(remote.Id);

        var status = runtime.BuildHomeStatus();
        Assert.Equal(PlayActionKind.Play, status.Play.Action);
        Assert.Equal("Play", status.Play.ButtonLabel);
        Assert.True(status.Play.Enabled);
        Assert.Null(status.Play.BlockReason);
        Assert.True(status.ClientReady);
        Assert.Equal(ServerLifecycleState.NotInstalled, status.ServerState);
    }

    [Fact]
    public void ResolvePlay_ClientUpdateRequired_WinsOverEverything()
    {
        using var tmp = new TempDir();
        var paths = tmp.Paths();
        paths.EnsureLayout();
        ManifestTestData.SaveClient(paths, new ClientManifest
        {
            Version = "req",
            Assets = [new ManagedAsset { Id = "core", DisplayName = "Core", Kind = ModKind.Mpq, Destination = "Data/patch-C.mpq", Required = true, Sha256 = new string('a', 64) }]
        });
        JsonStore.Save(paths.SettingsFile, new LauncherSettings { OnboardingCompleted = true });
        File.WriteAllText(Path.Combine(paths.Client, "WoW.exe"), "MZ");
        using var runtime = new LauncherRuntime(paths);

        Assert.True(runtime.Updates.ClientUpdateRequired);
        var status = runtime.BuildHomeStatus();
        Assert.True(status.ClientUpdateRequired);
        Assert.Equal(PlayActionKind.Update, status.Play.Action);
        Assert.Equal("Update", status.Play.ButtonLabel);
    }

    [Fact]
    public void PlayPipeline_CanPlay_ReturnsLocalizationKeys()
    {
        using var tmp = new TempDir();
        using var runtime = Create(tmp);
        var realm = runtime.Realms.Local;

        Assert.Equal("onboarding.continue", runtime.Play.CanPlay(realm, false).ButtonLabel);
        runtime.State.Settings.OnboardingCompleted = true;
        Assert.Equal("onboarding.locate", runtime.Play.CanPlay(realm, false).ButtonLabel);
        File.WriteAllText(Path.Combine(runtime.Paths.Client, "WoW.exe"), "MZ");
        Assert.Equal("update", runtime.Play.CanPlay(realm, true).ButtonLabel);
        Assert.Equal("install.server", runtime.Play.CanPlay(realm, false).ButtonLabel);

        var remote = new RealmEntry { Id = "r", Address = "x", ClientDirectoryOverride = tmp.Dir("nocli") };
        Assert.Equal("onboarding.locate", runtime.Play.CanPlay(remote, false).ButtonLabel);
        File.WriteAllText(Path.Combine(remote.ClientDirectoryOverride!, "WoW.exe"), "MZ");
        var ok = runtime.Play.CanPlay(remote, false);
        Assert.Equal(PlayActionKind.Play, ok.Action);
        Assert.Equal("play", ok.ButtonLabel);
        Assert.Equal("Play", runtime.Loc[ok.ButtonLabel]);
    }

    [Fact]
    public void Notify_RaisesChanged()
    {
        using var tmp = new TempDir();
        using var runtime = Create(tmp);
        var count = 0;
        runtime.Changed += () => count++;
        runtime.Notify();
        Assert.Equal(1, count);
    }

    [Fact]
    public void Dispose_IsIdempotentEnoughForUsing()
    {
        using var tmp = new TempDir();
        var runtime = Create(tmp);
        runtime.Dispose();
    }
}

public class PlayPipelineTests
{
    [Fact]
    public void ResolveGameExecutable_PrefersRealmChoice_ThenAnyKnownExe()
    {
        using var tmp = new TempDir();
        using var runtime = new LauncherRuntime(tmp.Paths());
        var client = tmp.Dir("client");
        File.WriteAllText(Path.Combine(client, "WoW.exe"), "MZ");
        File.WriteAllText(Path.Combine(client, "WoW_tweaked.exe"), "MZ2");

        Assert.Equal(Path.Combine(client, "WoW_tweaked.exe"),
            runtime.Play.ResolveGameExecutable(new RealmEntry { ClientExecutable = "WoW_tweaked.exe" }, client));
        Assert.Equal(Path.Combine(client, "WoW.exe"),
            runtime.Play.ResolveGameExecutable(new RealmEntry { ClientExecutable = "missing.exe" }, client));
        Assert.Throws<FileNotFoundException>(() =>
            runtime.Play.ResolveGameExecutable(new RealmEntry(), tmp.Dir("empty")));
    }

    [Fact]
    public async Task Execute_InvalidRealm_Throws()
    {
        using var tmp = new TempDir();
        using var runtime = new LauncherRuntime(tmp.Paths());
        await Assert.ThrowsAsync<InvalidOperationException>(() => runtime.Play.ExecuteAsync(new RealmEntry { Address = " " }));
        await Assert.ThrowsAsync<InvalidOperationException>(() => runtime.Play.ExecuteAsync(new RealmEntry { Address = "x", AuthPort = 0 }));
        await Assert.ThrowsAsync<InvalidOperationException>(() => runtime.Play.ExecuteAsync(new RealmEntry { Address = "x", ClientExecutable = "" }));
    }

    [Fact]
    public async Task Execute_NoClient_Throws()
    {
        using var tmp = new TempDir();
        using var runtime = new LauncherRuntime(tmp.Paths());
        var realm = new RealmEntry { Id = "r", Address = "x", ClientDirectoryOverride = tmp.Dir("nocli") };
        await Assert.ThrowsAsync<DirectoryNotFoundException>(() => runtime.Play.ExecuteAsync(realm));
    }

    [Fact]
    public async Task Execute_RequiredOutOfDate_ThrowsClientUpdateRequired_BeforeTouchingClient()
    {
        using var tmp = new TempDir();
        var paths = tmp.Paths();
        paths.EnsureLayout();
        ManifestTestData.SaveClient(paths, new ClientManifest
        {
            Assets = [new ManagedAsset { Id = "core", DisplayName = "Core", Kind = ModKind.Mpq, Destination = "Data/patch-C.mpq", Required = true, Sha256 = new string('a', 64) }]
        });
        using var runtime = new LauncherRuntime(paths);
        File.WriteAllText(Path.Combine(paths.Client, "WoW.exe"), "MZ");
        var remote = new RealmEntry { Id = "r", Address = "logon.example.org" };

        var ex = await Assert.ThrowsAsync<ClientUpdateRequiredException>(() => runtime.Play.ExecuteAsync(remote));
        Assert.Single(ex.OutdatedAssets);
        Assert.Contains("Core", ex.Message);
        Assert.False(File.Exists(Path.Combine(paths.Client, "realmlist.wtf")), "realmlist must not be written when play is blocked");
    }

    [Fact]
    public async Task Execute_RemoteRealm_WritesRealmlist_MaterializesMods_ClearsWdb_AndLaunches()
    {
        if (OperatingSystem.IsWindows())
            return; // The fake executable below is a shell script.

        using var tmp = new TempDir();
        var paths = tmp.Paths();
        ManifestTestData.SaveClient(paths, new ClientManifest
        {
            Assets = [new ManagedAsset
            {
                Id = "raid_visuals",
                Kind = ModKind.Mpq,
                Destination = "Data/patch-O.mpq",
                Optional = true
            }]
        });
        using var runtime = new LauncherRuntime(paths);
        var client = paths.Client;
        var exe = Path.Combine(client, "WoW.exe");
        File.WriteAllText(exe, "#!/bin/sh\nexit 0\n");
        File.SetUnixFileMode(exe, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        // The native test script needs no Wine/Proton download on the Linux runner.
        runtime.State.Settings.LinuxRunner = LinuxRunnerMode.Custom;
        runtime.State.Settings.WineRunnerPath = exe;
        Directory.CreateDirectory(Path.Combine(client, "Data", "enUS"));
        Directory.CreateDirectory(Path.Combine(client, "WDB"));
        File.WriteAllText(Path.Combine(client, "WDB", "creaturecache.wdb"), "stale");

        var store = paths.ModStore("raid_visuals");
        Directory.CreateDirectory(store);
        File.WriteAllText(Path.Combine(store, "patch-O.mpq"), "mpq");

        var remote = runtime.Realms.Add(new RealmEntry
        {
            DisplayName = "Remote",
            Address = "logon.example.org",
            AuthPort = 3725,
            ClearWdb = true,
            ManagedModState = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase) { ["raid_visuals"] = true }
        });
        var stages = new List<string>();
        runtime.Play.Stage += stages.Add;

        var result = await runtime.Play.ExecuteAsync(remote);

        Assert.NotNull(result.Game);
        Assert.Equal(exe, result.Game!.Executable);
        Assert.False(result.Game.ThroughLoader);
        Assert.False(result.ServerStartedByLauncher);
        Assert.NotEmpty(result.Log);
        Assert.Equal(result.Log, stages);

        Assert.Equal("set realmlist \"logon.example.org:3725\"\n", File.ReadAllText(Path.Combine(client, "realmlist.wtf")));
        Assert.Equal("set realmlist \"logon.example.org:3725\"\n", File.ReadAllText(Path.Combine(client, "Data", "enUS", "realmlist.wtf")));
        Assert.True(File.Exists(Path.Combine(client, "Data", "patch-O.mpq")));
        Assert.False(Directory.Exists(Path.Combine(client, "WDB")));
        Assert.True(File.Exists(Path.Combine(client, ClientManager.CleanBackupFileName)));
        Assert.Contains(stages, s => s.Contains("realmlist set to logon.example.org:3725", StringComparison.Ordinal));
        Assert.Contains(stages, s => s.Contains("Launched WoW.exe", StringComparison.Ordinal));
    }
}
