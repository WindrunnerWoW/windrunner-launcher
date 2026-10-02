using System.Net;
using System.Text.Json;
using WindrunnerLauncher.Core.Client;
using WindrunnerLauncher.Core.Downloads;
using WindrunnerLauncher.Core.Mods;
using WindrunnerLauncher.Core.Persistence;

namespace WindrunnerLauncher.Core.Tests;

public class ModManagerTests
{
    private sealed class Fixture : IDisposable
    {
        public TempDir Tmp { get; } = new();
        public LauncherPaths Paths { get; }
        public StateStore State { get; }
        public DownloadManager Downloads { get; }
        public ModManager Mods { get; }
        public RealmEntry Realm => State.SelectedRealm();
        public string ClientDir => Paths.Client;

        public Fixture(Action<LauncherPaths>? beforeLoad = null, HttpClient? http = null, bool seedTestCatalog = true)
        {
            Paths = Tmp.Paths();
            Paths.EnsureLayout();
            beforeLoad?.Invoke(Paths);
            if (seedTestCatalog && !File.Exists(Path.Combine(Paths.ManifestCache, ModManager.CachedEnvelopeFileName)))
                ManifestTestData.SaveDefaultClientCatalog(Paths);
            State = new StateStore(Paths);
            Downloads = new DownloadManager(http);
            File.WriteAllText(Path.Combine(Paths.Client, "WoW.exe"), "MZ");
            Directory.CreateDirectory(Path.Combine(Paths.Client, "Data"));
            Mods = new ModManager(Paths, State, Downloads);
        }

        public string Payload(string assetId, string fileName, string contents = "payload")
        {
            var store = Paths.ModStore(assetId);
            Directory.CreateDirectory(store);
            var path = Path.Combine(store, fileName);
            File.WriteAllText(path, contents);
            return path;
        }

        public string Live(params string[] parts) => Path.Combine([ClientDir, .. parts]);

        public void CacheVanillaFixes()
        {
            var src = Tmp.Dir("vanillafixes-src");
            File.WriteAllText(Path.Combine(src, "VanillaFixes.exe"), "loader");
            Directory.CreateDirectory(Paths.ModStore("vanillafixes"));
            ArchiveUtil.ZipDirectory(src, Path.Combine(Paths.ModStore("vanillafixes"), "vanillafixes.zip"));
        }

        public void Dispose()
        {
            Downloads.Dispose();
            Tmp.Dispose();
        }
    }

    [Fact]
    public void Constructor_DoesNotLoadBundledCatalog()
    {
        using var f = new Fixture(seedTestCatalog: false);
        Assert.Equal("none", f.Mods.ManifestSource.Origin);
        Assert.False(f.Mods.ManifestSource.Signed);
        Assert.Null(f.Mods.ManifestSource.Warning);
        Assert.Empty(f.Mods.Assets);
        Assert.Null(f.Mods.Find("raid_visuals"));
        Assert.Null(f.Mods.Find("nope"));
    }

    [Fact]
    public void SignedTestCatalog_IsWellFormed()
    {
        using var f = new Fixture();
        var manifest = f.Mods.Manifest;
        Assert.True(f.Mods.ManifestSource.Signed);
        Assert.Equal(ModManager.CachedEnvelopeFileName, f.Mods.ManifestSource.Origin);
        Assert.Equal(1, manifest.Schema);
        Assert.False(string.IsNullOrWhiteSpace(manifest.Version));

        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var asset in manifest.Assets)
        {
            Assert.True(ids.Add(asset.Id), $"duplicate asset id {asset.Id}");
            Assert.False(string.IsNullOrWhiteSpace(asset.DisplayName), asset.Id);
            foreach (var dep in asset.Dependencies)
                Assert.NotNull(f.Mods.Find(dep));
            foreach (var conflict in asset.Conflicts)
                Assert.NotNull(f.Mods.Find(conflict));
            Assert.DoesNotContain(asset.Id, asset.Dependencies, StringComparer.OrdinalIgnoreCase);
            if (asset.Kind == ModKind.Mpq)
            {
                Assert.True(MpqHandler.IsLetterPatch(ModPaths.LeafName(asset.Destination)), asset.Destination);
                Assert.StartsWith("Data/", asset.Destination);
            }
        }

        Assert.Contains(manifest.Assets, a => a.Kind == ModKind.Mpq);
        Assert.Contains(manifest.Assets, a => a.Kind == ModKind.Dll);
        Assert.DoesNotContain(manifest.Assets, a => a.Required);
    }

    [Fact]
    public async Task Enable_Mpq_MovesPayloadFromStoreIntoData()
    {
        using var f = new Fixture();
        var asset = f.Mods.Find("raid_visuals")!;
        Assert.Equal(ModKind.Mpq, asset.Kind);
        var source = f.Payload("raid_visuals", "patch-O.mpq", "mpq-bytes");
        Assert.False(f.Mods.IsApplied(f.Realm, asset));
        Assert.True(f.Mods.IsInstalled(f.Realm, asset));

        var log = await f.Mods.SetEnabledAsync(f.Realm, "raid_visuals", true);

        var live = f.Live("Data", "patch-O.mpq");
        Assert.True(File.Exists(live));
        Assert.Equal("mpq-bytes", File.ReadAllText(live));
        Assert.False(File.Exists(source), "enable is a move, not a copy");
        Assert.True(f.Mods.IsApplied(f.Realm, asset));
        Assert.True(f.Realm.ManagedModState["raid_visuals"]);
        Assert.Contains(log, l => l.Contains("Enabled patch-O.mpq", StringComparison.Ordinal));

        var reloaded = new StateStore(f.Paths);
        Assert.True(reloaded.SelectedRealm().ManagedModState["raid_visuals"]);
    }

    [Fact]
    public async Task Disable_Mpq_MovesPayloadBackIntoStore_NeverDeletes()
    {
        using var f = new Fixture();
        f.Payload("raid_visuals", "patch-O.mpq", "mpq-bytes");
        await f.Mods.SetEnabledAsync(f.Realm, "raid_visuals", true);

        var log = await f.Mods.SetEnabledAsync(f.Realm, "raid_visuals", false);

        Assert.False(File.Exists(f.Live("Data", "patch-O.mpq")));
        var stored = Path.Combine(f.Paths.ModStore("raid_visuals"), "patch-O.mpq");
        Assert.True(File.Exists(stored));
        Assert.Equal("mpq-bytes", File.ReadAllText(stored));
        Assert.False(f.Realm.ManagedModState["raid_visuals"]);
        Assert.Contains(log, l => l.Contains("Disabled patch-O.mpq", StringComparison.Ordinal));
        Assert.False(f.Mods.IsApplied(f.Realm, f.Mods.Find("raid_visuals")!));
        Assert.True(f.Mods.IsInstalled(f.Realm, f.Mods.Find("raid_visuals")!));
    }

    [Fact]
    public async Task Enable_Mpq_WithoutPayload_RequiresDownload()
    {
        using var f = new Fixture();
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            f.Mods.SetEnabledAsync(f.Realm, "pretty_night_sky", true));
        Assert.Contains("mods/pretty_night_sky", ex.Message);
        Assert.False(File.Exists(f.Live("Data", "patch-Z.mpq")));
        Assert.False(f.Realm.ManagedModState.ContainsKey("pretty_night_sky"));
    }

    [Fact]
    public async Task Enable_PullsInDependencies()
    {
        using var f = new Fixture();
        f.Payload("hd_patch_a", "patch-H.mpq");
        f.Payload("vanilla_helpers", "patch-V.mpq");

        await f.Mods.SetEnabledAsync(f.Realm, "hd_patch_a", true);

        Assert.True(File.Exists(f.Live("Data", "patch-H.mpq")));
        Assert.True(File.Exists(f.Live("Data", "patch-V.mpq")), "dependency is materialized too");
        Assert.True(f.Realm.ManagedModState["vanilla_helpers"]);
    }

    [Fact]
    public async Task Disable_Dependency_DisablesDependents()
    {
        using var f = new Fixture();
        f.Payload("hd_patch_a", "patch-H.mpq");
        f.Payload("vanilla_helpers", "patch-V.mpq");
        await f.Mods.SetEnabledAsync(f.Realm, "hd_patch_a", true);

        await f.Mods.SetEnabledAsync(f.Realm, "vanilla_helpers", false);

        Assert.False(f.Realm.ManagedModState["hd_patch_a"]);
        Assert.False(File.Exists(f.Live("Data", "patch-H.mpq")));
        Assert.False(File.Exists(f.Live("Data", "patch-V.mpq")));
        Assert.True(File.Exists(Path.Combine(f.Paths.ModStore("hd_patch_a"), "patch-H.mpq")));
        Assert.True(File.Exists(Path.Combine(f.Paths.ModStore("vanilla_helpers"), "patch-V.mpq")));
    }

    [Fact]
    public async Task Enable_TurnsOffConflicts()
    {
        using var f = new Fixture();
        var src = f.Tmp.Dir("vf-conflict-src");
        File.WriteAllText(Path.Combine(src, "VanillaFixes.exe"), "loader");
        Directory.CreateDirectory(f.Paths.ModStore("vanillafixes"));
        ArchiveUtil.ZipDirectory(src, Path.Combine(f.Paths.ModStore("vanillafixes"), "vanillafixes.zip"));
        f.Realm.ManagedModState["dxvk"] = true;
        await f.Mods.SetEnabledAsync(f.Realm, "vanillafixes", true);
        Assert.True(f.Realm.ManagedModState["vanillafixes"]);
        // Windows drivers cannot use both. Under Wine the launcher leaves the pair enabled.
        if (OperatingSystem.IsLinux())
            Assert.True(f.Realm.ManagedModState["dxvk"]);
        else
            Assert.False(f.Realm.ManagedModState["dxvk"]);
    }

    [Fact]
    public async Task Download_ThenEnableDisable_ReusesCachedPayloadWithoutAnotherRequest()
    {
        var bytes = Encoding.UTF8.GetBytes("downloaded-mod");
        var handler = new CountingHandler(bytes);
        using var http = new HttpClient(handler);
        using var f = new Fixture(paths =>
        {
            ManifestTestData.SaveClient(paths, new ClientManifest
            {
                Assets =
                [
                    new ManagedAsset
                    {
                        Id = "offline_mod",
                        DisplayName = "Offline mod",
                        Kind = ModKind.Mpq,
                        Destination = "Data/patch-O.mpq",
                        DownloadUrl = "https://example.test/patch-O.mpq",
                        Sha256 = Security.Checksums.Sha256Hex(bytes)
                    }
                ]
            });
        }, http);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            f.Mods.SetEnabledAsync(f.Realm, "offline_mod", true));
        Assert.Equal(0, handler.Calls);

        await f.Mods.DownloadAssetAsync(f.Realm, "offline_mod");
        Assert.Equal(1, handler.Calls);
        Assert.False(f.Realm.ManagedModState.ContainsKey("offline_mod"));
        Assert.True(File.Exists(Path.Combine(f.Paths.ModStore("offline_mod"), "patch-O.mpq")));
        Assert.False(File.Exists(f.Live("Data", "patch-O.mpq")));

        await f.Mods.SetEnabledAsync(f.Realm, "offline_mod", true);
        await f.Mods.SetEnabledAsync(f.Realm, "offline_mod", false);
        await f.Mods.SetEnabledAsync(f.Realm, "offline_mod", true);
        Assert.Equal(1, handler.Calls);
        Assert.Equal("downloaded-mod", File.ReadAllText(f.Live("Data", "patch-O.mpq")));
    }

    private sealed class CountingHandler(byte[] payload) : HttpMessageHandler
    {
        public int Calls { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(payload)
            });
        }
    }

    [Fact]
    public async Task SetEnabled_UnknownAsset_Throws()
    {
        using var f = new Fixture();
        await Assert.ThrowsAsync<KeyNotFoundException>(() => f.Mods.SetEnabledAsync(f.Realm, "ghost", true));
    }

    [Fact]
    public async Task Materialize_PerRealm_SwapsMpqsBetweenRealms()
    {
        using var f = new Fixture();
        f.Payload("raid_visuals", "patch-O.mpq");
        f.Payload("pretty_night_sky", "patch-Z.mpq");
        var local = f.Realm;
        var remote = new RealmEntry { Id = "remote", DisplayName = "Remote", Address = "x" };
        f.State.Realms.Realms.Add(remote);
        local.ManagedModState["raid_visuals"] = true;
        remote.ManagedModState["pretty_night_sky"] = true;

        await f.Mods.MaterializeAsync(local);
        Assert.True(File.Exists(f.Live("Data", "patch-O.mpq")));
        Assert.False(File.Exists(f.Live("Data", "patch-Z.mpq")));

        await f.Mods.MaterializeAsync(remote);
        Assert.False(File.Exists(f.Live("Data", "patch-O.mpq")));
        Assert.True(File.Exists(f.Live("Data", "patch-Z.mpq")));
        Assert.True(File.Exists(Path.Combine(f.Paths.ModStore("raid_visuals"), "patch-O.mpq")));

        await f.Mods.MaterializeAsync(local);
        Assert.True(File.Exists(f.Live("Data", "patch-O.mpq")));
        Assert.False(File.Exists(f.Live("Data", "patch-Z.mpq")));
    }

    [Fact]
    public async Task Materialize_MissingClientDir_LogsAndReturns()
    {
        using var f = new Fixture();
        var realm = new RealmEntry { Id = "r", ClientDirectoryOverride = f.Tmp.Combine("nowhere") };
        var log = await f.Mods.MaterializeAsync(realm);
        Assert.Single(log);
        Assert.Contains("does not exist", log[0]);
    }

    [Fact]
    public async Task Dll_EnableDisable_MaintainsDllsTxt()
    {
        using var f = new Fixture();
        f.Payload("nampower", "nampower.dll", "dll");
        f.CacheVanillaFixes();

        await f.Mods.SetEnabledAsync(f.Realm, "nampower", true);
        Assert.True(File.Exists(f.Live("nampower.dll")));
        Assert.True(DllsTxt.Contains(f.ClientDir, "nampower.dll"));
        Assert.True(f.Realm.ManagedModState["vanillafixes"], "nampower depends on vanillafixes");

        await f.Mods.SetEnabledAsync(f.Realm, "nampower", false);
        Assert.False(File.Exists(f.Live("nampower.dll")));
        Assert.False(DllsTxt.Contains(f.ClientDir, "nampower.dll"));
        Assert.True(File.Exists(Path.Combine(f.Paths.ModStore("nampower"), "nampower.dll")));
    }

    [Fact]
    public async Task AddOn_EnableDisable_MovesDirectory()
    {
        using var f = new Fixture();
        var store = Path.Combine(f.Paths.ModStore("pfquest_turtle"), "pfQuest-turtle");
        Directory.CreateDirectory(store);
        File.WriteAllText(Path.Combine(store, "pfQuest-turtle.toc"), "## Interface: 11200");

        await f.Mods.SetEnabledAsync(f.Realm, "pfquest_turtle", true);
        Assert.True(File.Exists(f.Live("Interface", "AddOns", "pfQuest-turtle", "pfQuest-turtle.toc")));
        Assert.False(Directory.Exists(store));

        await f.Mods.SetEnabledAsync(f.Realm, "pfquest_turtle", false);
        Assert.False(Directory.Exists(f.Live("Interface", "AddOns", "pfQuest-turtle")));
        Assert.True(File.Exists(Path.Combine(store, "pfQuest-turtle.toc")));
    }

    [Fact]
    public async Task Configuration_EnableDisable_EditsConfigWtf()
    {
        using var f = new Fixture();
        var config = f.Live("WTF", "Config.wtf");
        Directory.CreateDirectory(Path.GetDirectoryName(config)!);
        File.WriteAllText(config, "SET locale \"enUS\"\r\n");

        await f.Mods.SetEnabledAsync(f.Realm, "script_memory", true);
        var values = Client.ConfigWtf.Read(config);
        Assert.Equal("16384", values["scriptMemory"]);
        Assert.Equal("131072", values["maxAddOnMemory"]);
        Assert.Equal("enUS", values["locale"]);
        Assert.True(f.Mods.IsApplied(f.Realm, f.Mods.Find("script_memory")!));

        await f.Mods.SetEnabledAsync(f.Realm, "script_memory", false);
        values = Client.ConfigWtf.Read(config);
        Assert.False(values.ContainsKey("scriptMemory"));
        Assert.Equal("enUS", values["locale"]);
    }

    [Fact]
    public async Task VanillaTweaks_ReappliesWhenSettingsChange()
    {
        using var f = new Fixture();
        var clean = f.Live(ClientManager.CleanBackupFileName);
        File.WriteAllText(clean, "clean");
        File.WriteAllText(f.Live("WoW.exe"), "patched");
        f.Realm.ManagedModState["vanilla_tweaks"] = true;
        VanillaTweaks.RecordAppliedState(f.ClientDir, clean, f.State.Settings.VanillaTweaks);

        var unchanged = await f.Mods.MaterializeAsync(f.Realm);
        Assert.DoesNotContain(unchanged, line => line.Contains("vanilla-tweaks.exe", StringComparison.OrdinalIgnoreCase));

        f.State.Settings.VanillaTweaks.UseRecommendedPreset = false;
        f.State.Settings.VanillaTweaks.FarClip++;
        var changed = await f.Mods.MaterializeAsync(f.Realm);
        Assert.Contains(changed, line => line.Contains("vanilla-tweaks.exe", StringComparison.OrdinalIgnoreCase));
        Assert.False(VanillaTweaks.IsCurrent(f.ClientDir, clean, f.State.Settings.VanillaTweaks));
    }

    [Fact]
    public async Task ZipRoot_EnableDisable_TracksExtractedFiles()
    {
        using var f = new Fixture();
        var store = f.Paths.ModStore("vanillafixes");
        Directory.CreateDirectory(store);
        var src = f.Tmp.Dir("vf-src");
        File.WriteAllText(Path.Combine(src, "VanillaFixes.exe"), "loader");
        File.WriteAllText(Path.Combine(src, "VfPatcher.dll"), "dll");
        ArchiveUtil.ZipDirectory(src, Path.Combine(store, "vanillafixes-2.2.0.zip"));
        File.WriteAllText(f.Live("user.txt"), "mine");

        await f.Mods.SetEnabledAsync(f.Realm, "vanillafixes", true);
        Assert.True(File.Exists(f.Live("VanillaFixes.exe")));
        Assert.True(File.Exists(f.Live("VfPatcher.dll")));
        Assert.True(f.Mods.IsApplied(f.Realm, f.Mods.Find("vanillafixes")!));

        await f.Mods.SetEnabledAsync(f.Realm, "vanillafixes", false);
        Assert.False(File.Exists(f.Live("VanillaFixes.exe")));
        Assert.False(File.Exists(f.Live("VfPatcher.dll")));
        Assert.True(File.Exists(f.Live("user.txt")), "only tracked files are removed");
        Assert.True(File.Exists(f.Live("WoW.exe")));
    }

    [Fact]
    public async Task MultiMonitorFix_ExtractsDllAndMonitorFile_AndRegistersDllsTxt()
    {
        using var f = new Fixture();
        var store = f.Paths.ModStore("multimonitor");
        f.CacheVanillaFixes();
        Directory.CreateDirectory(store);
        var src = f.Tmp.Dir("vmm");
        File.WriteAllText(Path.Combine(src, "VanillaMultiMonitorFix.dll"), "dll");
        File.WriteAllText(Path.Combine(src, "VMMFix_preferred_monitor.txt"), "0");
        ArchiveUtil.ZipDirectory(src, Path.Combine(store, "release.zip"));

        await f.Mods.SetEnabledAsync(f.Realm, "multimonitor", true);

        Assert.Equal("dll", File.ReadAllText(f.Live("VanillaMultiMonitorFix.dll")));
        Assert.Equal("0", File.ReadAllText(f.Live("VMMFix_preferred_monitor.txt")));
        Assert.True(DllsTxt.Contains(f.ClientDir, "VanillaMultiMonitorFix.dll"));
        Assert.True(f.Realm.ManagedModState["vanillafixes"], "the fix is loaded by VanillaFixes");

        await f.Mods.SetEnabledAsync(f.Realm, "multimonitor", false);

        Assert.False(File.Exists(f.Live("VanillaMultiMonitorFix.dll")));
        Assert.False(File.Exists(f.Live("VMMFix_preferred_monitor.txt")));
        Assert.False(DllsTxt.Contains(f.ClientDir, "VanillaMultiMonitorFix.dll"));
        Assert.True(File.Exists(Path.Combine(store, "release.zip")));
    }

    [Fact]
    public void DesiredState_RequiredAlwaysOn_RealmOverridesDefault()
    {
        using var f = new Fixture();
        var realm = new RealmEntry { Id = "r" };
        var required = new ManagedAsset { Id = "req", Required = true, DefaultEnabled = false };
        var optionalOn = new ManagedAsset { Id = "on", DefaultEnabled = true };
        var optionalOff = new ManagedAsset { Id = "off", DefaultEnabled = false };

        Assert.True(f.Mods.DesiredState(realm, required));
        Assert.True(f.Mods.DesiredState(realm, optionalOn));
        Assert.False(f.Mods.DesiredState(realm, optionalOff));

        realm.ManagedModState["on"] = false;
        realm.ManagedModState["off"] = true;
        realm.ManagedModState["req"] = false;
        Assert.False(f.Mods.DesiredState(realm, optionalOn));
        Assert.True(f.Mods.DesiredState(realm, optionalOff));
        Assert.True(f.Mods.DesiredState(realm, required));
    }

    [Fact]
    public void ResolveDesiredState_DependenciesAndConflicts()
    {
        using var f = new Fixture();
        var realm = new RealmEntry { Id = "r" };
        realm.ManagedModState["hd_patch_a"] = true;
        var desired = f.Mods.ResolveDesiredState(realm);
        Assert.True(desired["hd_patch_a"]);
        Assert.True(desired["vanilla_helpers"], "dependency pulled in");
        Assert.False(desired["raid_visuals"]);

        realm.ManagedModState.Clear();
        realm.ManagedModState["vanillafixes"] = true;
        realm.ManagedModState["dxvk"] = true;
        desired = f.Mods.ResolveDesiredState(realm);
        if (OperatingSystem.IsLinux())
        {
            Assert.True(desired["vanillafixes"]);
            Assert.True(desired["dxvk"]);
        }
        else
            Assert.NotEqual(desired["vanillafixes"], desired["dxvk"]);
    }

    [Fact]
    public void EnabledCountFor_CountsOnlySupportedKinds()
    {
        using var f = new Fixture();
        var realm = new RealmEntry { Id = "r" };
        Assert.Equal(0, f.Mods.EnabledCountFor(realm));
        realm.ManagedModState["raid_visuals"] = true;
        realm.ManagedModState["dxvk"] = true;
        Assert.Equal(2, f.Mods.EnabledCountFor(realm));
    }

    [Fact]
    public void DetectUnmanaged_FindsForeignDllsAndAddons_MpqFilesHaveSeparateControls()
    {
        using var f = new Fixture();
        File.WriteAllText(f.Live("Data", "patch-3.mpq"), "official");
        File.WriteAllText(f.Live("Data", "patch-Q.mpq"), "foreign");
        File.WriteAllText(f.Live("Data", "patch-O.mpq"), "managed");
        File.WriteAllText(f.Live("fmod.dll"), "stock");
        File.WriteAllText(f.Live("nampower.dll"), "managed");
        File.WriteAllText(f.Live("SomeHack.dll"), "foreign");
        Directory.CreateDirectory(f.Live("Interface", "AddOns", "pfQuest-turtle"));
        Directory.CreateDirectory(f.Live("Interface", "AddOns", "MyAddon"));

        var unmanaged = f.Mods.DetectUnmanaged(f.Realm);

        Assert.Equal(2, unmanaged.Count);
        Assert.DoesNotContain(unmanaged, u => u.Kind == ModKind.Mpq);
        Assert.Contains(f.Mods.ListClientMpqs(f.Realm), item => item.FileName == "patch-Q.mpq");
        Assert.Contains(unmanaged, u => u.Name == "SomeHack.dll" && u.Kind == ModKind.Dll);
        Assert.Contains(unmanaged, u => u.Name == "MyAddon" && u.Kind == ModKind.AddOn);

        var items = f.Mods.ListItems(f.Realm);
        var foreign = items.Where(i => i.Unmanaged).ToList();
        Assert.Equal(2, foreign.Count);
        Assert.All(foreign, i => Assert.StartsWith("unmanaged:", i.Asset.Id));
        Assert.Contains(items, i => i.Asset.Id == "raid_visuals" && i.Installed);
    }

    [Fact]
    public void Unsigned_LegacyClientCache_IsIgnored()
    {
        using var f = new Fixture(paths =>
        {
            var manifest = new ClientManifest
            {
                Version = "cached-1",
                Assets =
                [
                    new ManagedAsset { Id = "core-fix", DisplayName = "Core fix", Kind = ModKind.Mpq, Destination = "Data/patch-C.mpq", Required = true, Sha256 = Security.Checksums.Sha256Hex(Encoding.ASCII.GetBytes("core")) }
                ]
            };
            JsonStore.Save(Path.Combine(paths.ManifestCache, "client.json"), manifest);
        }, seedTestCatalog: false);

        Assert.Equal("none", f.Mods.ManifestSource.Origin);
        Assert.False(f.Mods.ManifestSource.Signed);
        Assert.Null(f.Mods.ManifestSource.Warning);
        Assert.NotEqual("cached-1", f.Mods.Manifest.Version);
        Assert.Empty(f.Mods.RequiredAssets);
    }

    [Fact]
    public async Task RequiredAsset_CannotBeDisabled_AndIsReportedOutOfDate()
    {
        using var f = new Fixture(paths =>
        {
            var manifest = new ClientManifest
            {
                Version = "cached-2",
                Assets =
                [
                    new ManagedAsset { Id = "core-fix", DisplayName = "Core fix", Kind = ModKind.Mpq, Destination = "Data/patch-C.mpq", Required = true, Sha256 = Security.Checksums.Sha256Hex(Encoding.ASCII.GetBytes("core")) }
                ]
            };
            ManifestTestData.SaveClient(paths, manifest);
        });

        var stale = f.Mods.RequiredOutOfDate(f.Realm);
        Assert.Single(stale);
        Assert.Equal("core-fix", stale[0].Id);

        await Assert.ThrowsAsync<InvalidOperationException>(() => f.Mods.SetEnabledAsync(f.Realm, "core-fix", false));

        // Wrong content: applied but checksum mismatch -> still out of date.
        File.WriteAllText(f.Live("Data", "patch-C.mpq"), "tampered");
        Assert.Single(f.Mods.RequiredOutOfDate(f.Realm));

        File.WriteAllText(f.Live("Data", "patch-C.mpq"), "core");
        Assert.Empty(f.Mods.RequiredOutOfDate(f.Realm));
    }

    [Fact]
    public void SignedSidecar_IsIgnored()
    {
        var payload = JsonSerializer.Serialize(new ClientManifest { Version = "signed-9" }, JsonStore.Options);

        using var f = new Fixture(paths =>
        {
            File.WriteAllText(Path.Combine(paths.ManifestCache, "client.json"), payload);
            var sidecar = DevSigningKeys.SignEnvelope(TrustDomain.Client, payload);
            sidecar.PayloadJson = "";
            JsonStore.Save(Path.Combine(paths.ManifestCache, "client.json.sig"), sidecar);
        }, seedTestCatalog: false);

        Assert.False(f.Mods.ManifestSource.Signed);
        Assert.Equal("none", f.Mods.ManifestSource.Origin);
        Assert.NotEqual("signed-9", f.Mods.Manifest.Version);
    }

    [Fact]
    public void SignedEnvelope_WithDevKey_IsTrusted()
    {
        var payload = JsonSerializer.Serialize(new ClientManifest { Version = "env-1" }, JsonStore.Options);
        using var f = new Fixture(paths =>
            JsonStore.Save(Path.Combine(paths.ManifestCache, ModManager.CachedEnvelopeFileName), DevSigningKeys.SignEnvelope(TrustDomain.Client, payload)));

        Assert.True(f.Mods.ManifestSource.Signed);
        Assert.Equal(ModManager.CachedEnvelopeFileName, f.Mods.ManifestSource.Origin);
        Assert.Equal("env-1", f.Mods.Manifest.Version);
    }

    [Fact]
    public void SignedEnvelope_TamperedPayload_IsIgnoredWithWarning()
    {
        var payload = JsonSerializer.Serialize(new ClientManifest { Version = "evil" }, JsonStore.Options);
        using var f = new Fixture(paths =>
        {
            var envelope = DevSigningKeys.SignEnvelope(TrustDomain.Client, payload);
            envelope.PayloadJson = payload.Replace("evil", "eviL");
            JsonStore.Save(Path.Combine(paths.ManifestCache, ModManager.CachedEnvelopeFileName), envelope);
        });

        Assert.Equal("none", f.Mods.ManifestSource.Origin);
        Assert.False(f.Mods.ManifestSource.Signed);
        Assert.Contains("signature", f.Mods.ManifestSource.Warning, StringComparison.OrdinalIgnoreCase);
        Assert.NotEqual("evil", f.Mods.Manifest.Version);
        Assert.NotEqual("eviL", f.Mods.Manifest.Version);
        Assert.Empty(f.Mods.Assets);
    }

    [Fact]
    public void SignedEnvelope_WithServerKey_IsRejectedForClientDomain()
    {
        var payload = JsonSerializer.Serialize(new ClientManifest { Version = "wrong-domain" }, JsonStore.Options);
        using var f = new Fixture(paths =>
            JsonStore.Save(Path.Combine(paths.ManifestCache, ModManager.CachedEnvelopeFileName), DevSigningKeys.SignEnvelope(TrustDomain.Server, payload)));

        Assert.Equal("none", f.Mods.ManifestSource.Origin);
        Assert.NotNull(f.Mods.ManifestSource.Warning);
        Assert.NotEqual("wrong-domain", f.Mods.Manifest.Version);
    }

    [Fact]
    public void SignedEnvelope_WithUntrustedKey_IsRejected()
    {
        var (_, priv) = Security.Ed25519Signer.GenerateKeyPair();
        var payload = JsonSerializer.Serialize(new ClientManifest { Version = "rogue" }, JsonStore.Options);
        using var f = new Fixture(paths =>
        {
            var envelope = new SignedManifestEnvelope
            {
                KeyId = DevSigningKeys.ClientKeyId,
                SignatureB64 = Convert.ToBase64String(Security.Ed25519Signer.Sign(priv, Encoding.UTF8.GetBytes(payload))),
                PayloadJson = payload
            };
            JsonStore.Save(Path.Combine(paths.ManifestCache, ModManager.CachedEnvelopeFileName), envelope);
        });

        Assert.Equal("none", f.Mods.ManifestSource.Origin);
        Assert.NotEqual("rogue", f.Mods.Manifest.Version);
    }

    [Theory]
    [InlineData("patch-A.mpq", true)]
    [InlineData("Data/patch-z.MPQ", true)]
    [InlineData("patch-2.mpq", false)]
    [InlineData("patch.mpq", false)]
    [InlineData("patch-AB.mpq", false)]
    [InlineData("base.mpq", false)]
    public void MpqHandler_IsLetterPatch(string name, bool expected) => Assert.Equal(expected, MpqHandler.IsLetterPatch(name));

    [Theory]
    [InlineData("Data/patch.mpq")]
    [InlineData("Data/patch-2.mpq")]
    [InlineData("Data/base.mpq")]
    [InlineData("Data/patch-AB.mpq")]
    [InlineData("")]
    public void MpqHandler_Guard_RejectsBaseGameData(string destination)
    {
        using var f = new Fixture();
        var ctx = new ModApplyContext
        {
            Paths = f.Paths,
            State = f.State,
            Asset = new ManagedAsset { Id = "x", Kind = ModKind.Mpq, Destination = destination },
            ClientDir = f.ClientDir,
            StoreDir = f.Paths.ModStore("x")
        };
        Assert.Throws<InvalidOperationException>(() => MpqHandler.Guard(ctx));
    }

    [Fact]
    public void PayloadFileName_PrefersUrlThenDestinationThenId()
    {
        Assert.Equal("vf.zip", ModManager.PayloadFileName(new ManagedAsset { Id = "a", DownloadUrl = "https://example.invalid/dl/vf.zip?x=1", Destination = "Data/patch-A.mpq" }));
        Assert.Equal("patch-A.mpq", ModManager.PayloadFileName(new ManagedAsset { Id = "a", Destination = "Data/patch-A.mpq" }));
        Assert.Equal("a.zip", ModManager.PayloadFileName(new ManagedAsset { Id = "a", Destination = "" }));
    }

    [Fact]
    public void ModPaths_NormalizeAndLeaf()
    {
        Assert.Equal(Path.Combine("Data", "patch-A.mpq"), ModPaths.Normalize("/Data\\patch-A.mpq/"));
        Assert.Equal("patch-A.mpq", ModPaths.LeafName("Data/patch-A.mpq"));
        Assert.Equal("", ModPaths.LeafName(""));
    }

    [Fact]
    public void ConfigurationHandler_ParseValues()
    {
        var values = ConfigurationHandler.ParseValues(new ManagedAsset { AssetContains = " a=1; b = \"two\" ;;novalue; =x " });
        Assert.Equal(2, values.Count);
        Assert.Equal("1", values["a"]);
        Assert.Equal("two", values["B"]);
        Assert.Empty(ConfigurationHandler.ParseValues(new ManagedAsset()));
    }
}
