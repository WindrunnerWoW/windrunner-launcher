using WindrunnerLauncher.Core.Downloads;
using WindrunnerLauncher.Core.Mods;
using WindrunnerLauncher.Core.Persistence;

namespace WindrunnerLauncher.Core.Tests;

public class ClientMpqTests
{
    [Fact]
    public async Task OptionalMpq_DisableAndEnable_MovesWithoutDeleting_AndPersistsPerRealm()
    {
        using var tmp = new TempDir();
        var paths = tmp.Paths();
        paths.EnsureLayout();
        var data = tmp.Dir("client/Data");
        File.WriteAllText(Path.Combine(data, "custom.MPQ"), "original bytes");
        var state = new StateStore(paths);
        using var downloads = new DownloadManager();
        var mods = new ModManager(paths, state, downloads);
        var realm = state.SelectedRealm();

        await mods.SetClientMpqEnabledAsync(realm, "custom.MPQ", false);
        var parked = Path.Combine(data, ModManager.DisabledMpqFolderName, "custom.MPQ");
        Assert.False(File.Exists(Path.Combine(data, "custom.MPQ")));
        Assert.Equal("original bytes", File.ReadAllText(parked));
        Assert.False(new StateStore(paths).SelectedRealm().MpqFileState["custom.mpq"]);

        await mods.SetClientMpqEnabledAsync(realm, "custom.MPQ", true);
        Assert.False(File.Exists(parked));
        Assert.Equal("original bytes", File.ReadAllText(Path.Combine(data, "custom.MPQ")));
        Assert.True(new StateStore(paths).SelectedRealm().MpqFileState["custom.mpq"]);
    }

    [Fact]
    public async Task NumericPatches_AreRequired_AndParkedPatchSixIsRestored()
    {
        using var tmp = new TempDir();
        var paths = tmp.Paths();
        paths.EnsureLayout();
        var data = tmp.Dir("client/Data");
        var parked = tmp.Dir("client/Data/DisabledMPQs");
        foreach (var name in new[] { "patch-1.mpq", "patch-5.mpq", "patch-9.mpq" })
            File.WriteAllText(Path.Combine(data, name), name);
        File.WriteAllText(Path.Combine(parked, "patch-6.mpq"), "broken but required");
        var state = new StateStore(paths);
        using var downloads = new DownloadManager();
        var mods = new ModManager(paths, state, downloads);
        var realm = state.SelectedRealm();

        Assert.All(mods.ListClientMpqs(realm), item => Assert.True(item.Required));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            mods.SetClientMpqEnabledAsync(realm, "patch-5.mpq", false));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            mods.SetClientMpqEnabledAsync(realm, "patch-6.mpq", false));
        Assert.True(File.Exists(Path.Combine(data, "patch-5.mpq")));

        mods.MaterializeClientMpqs(realm);
        Assert.Equal("broken but required", File.ReadAllText(Path.Combine(data, "patch-6.mpq")));
        Assert.False(File.Exists(Path.Combine(parked, "patch-6.mpq")));
        Assert.DoesNotContain(mods.ListClientMpqs(realm), item => item.FileName == "patch-7.mpq");
    }

    [Fact]
    public async Task PlayMaterialization_SwitchesOptionalMpqBetweenRealms()
    {
        using var tmp = new TempDir();
        var paths = tmp.Paths();
        paths.EnsureLayout();
        var data = tmp.Dir("client/Data");
        File.WriteAllText(Path.Combine(data, "patch-10.mpq"), "optional");
        var state = new StateStore(paths);
        var local = state.SelectedRealm();
        var remote = new RealmEntry { Id = "remote", DisplayName = "Remote" };
        remote.MpqFileState["patch-10.mpq"] = false;
        state.Realms.Realms.Add(remote);
        state.SaveRealms();
        using var downloads = new DownloadManager();
        var mods = new ModManager(paths, state, downloads);

        Assert.False(mods.ListClientMpqs(remote).Single().Required);
        await mods.MaterializeAsync(remote);
        Assert.False(File.Exists(Path.Combine(data, "patch-10.mpq")));
        Assert.True(File.Exists(Path.Combine(data, ModManager.DisabledMpqFolderName, "patch-10.mpq")));

        await mods.MaterializeAsync(local);
        Assert.True(File.Exists(Path.Combine(data, "patch-10.mpq")));
        Assert.False(File.Exists(Path.Combine(data, ModManager.DisabledMpqFolderName, "patch-10.mpq")));
        Assert.False(remote.MpqFileState["patch-10.mpq"]);
    }

    [Fact]
    public void ManagedPatches_AreOnlyShownInTheManagedModList()
    {
        using var tmp = new TempDir();
        var paths = tmp.Paths();
        paths.EnsureLayout();
        var data = tmp.Dir("client/Data");
        File.WriteAllText(Path.Combine(data, "patch-O.mpq"), "managed");
        File.WriteAllText(Path.Combine(data, "extra.mpq"), "external");
        ManifestTestData.SaveDefaultClientCatalog(paths);
        var state = new StateStore(paths);
        using var downloads = new DownloadManager();
        var mods = new ModManager(paths, state, downloads);

        var mpqs = mods.ListClientMpqs(state.SelectedRealm());
        Assert.Contains(mpqs, item => item.FileName == "extra.mpq");
        Assert.DoesNotContain(mpqs, item => item.FileName == "patch-O.mpq");
    }

    [Fact]
    public async Task DuplicateActiveAndParkedMpq_IsNeverOverwritten()
    {
        using var tmp = new TempDir();
        var paths = tmp.Paths();
        paths.EnsureLayout();
        var data = tmp.Dir("client/Data");
        var parked = tmp.Dir("client/Data/DisabledMPQs");
        File.WriteAllText(Path.Combine(data, "extra.mpq"), "active");
        File.WriteAllText(Path.Combine(parked, "extra.mpq"), "parked");
        var state = new StateStore(paths);
        using var downloads = new DownloadManager();
        var mods = new ModManager(paths, state, downloads);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            mods.SetClientMpqEnabledAsync(state.SelectedRealm(), "extra.mpq", false));
        Assert.Equal("active", File.ReadAllText(Path.Combine(data, "extra.mpq")));
        Assert.Equal("parked", File.ReadAllText(Path.Combine(parked, "extra.mpq")));
    }
}
