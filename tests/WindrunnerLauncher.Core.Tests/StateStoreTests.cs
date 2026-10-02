using WindrunnerLauncher.Core.Persistence;

namespace WindrunnerLauncher.Core.Tests;

public class StateStoreTests
{
    [Fact]
    public void FreshDirectory_AlwaysHasLocalRealm()
    {
        using var tmp = new TempDir();
        var store = new StateStore(tmp.Paths());

        var local = Assert.Single(store.Realms.Realms);
        Assert.Equal("local", local.Id);
        Assert.Equal(RealmEntry.LocalServerId, local.Id);
        Assert.Equal("Windrunner", local.DisplayName);
        Assert.Equal("127.0.0.1", local.Address);
        Assert.Equal(3724, local.AuthPort);
    }

    [Fact]
    public void FreshDirectory_DoesNotWriteFilesUntilSaved()
    {
        using var tmp = new TempDir();
        var paths = tmp.Paths();
        var store = new StateStore(paths);
        Assert.False(File.Exists(paths.SettingsFile));
        Assert.False(File.Exists(paths.RealmsFile));
        Assert.Null(store.Rollback);

        store.SaveSettings();
        store.SaveRealms();
        Assert.True(File.Exists(paths.SettingsFile));
        Assert.True(File.Exists(paths.RealmsFile));
    }

    [Fact]
    public void RealmsFileWithoutLocal_LocalIsReinsertedAtIndexZero_AndPersisted()
    {
        using var tmp = new TempDir();
        var paths = tmp.Paths();
        JsonStore.Save(paths.RealmsFile, new RealmListState
        {
            Realms = [new RealmEntry { Id = "remote-1", DisplayName = "Remote", Address = "example.invalid" }]
        });
        JsonStore.Save(paths.SettingsFile, new LauncherSettings
        {
            Server = new ServerFriendlySettings { RealmAddress = "10.0.0.7", AuthPort = 3799, RealmName = "Custom" }
        });

        var store = new StateStore(paths);

        Assert.Equal(2, store.Realms.Realms.Count);
        var local = store.Realms.Realms[0];
        Assert.Equal(RealmEntry.LocalServerId, local.Id);
        Assert.Equal("10.0.0.7", local.Address);
        Assert.Equal(3799, local.AuthPort);
        Assert.Equal("Custom", local.InGameRealmName);
        Assert.Equal("remote-1", store.Realms.Realms[1].Id);

        // Ensure() must have persisted the repaired list.
        var reloaded = JsonStore.LoadOrNew(paths.RealmsFile, () => new RealmListState());
        Assert.Equal(2, reloaded.Realms.Count);
        Assert.Equal(RealmEntry.LocalServerId, reloaded.Realms[0].Id);
    }

    [Fact]
    public void EmptyRealmsList_LocalIsCreated()
    {
        using var tmp = new TempDir();
        var paths = tmp.Paths();
        tmp.File("config/realms.json", "{ \"realms\": [] }");
        var store = new StateStore(paths);
        Assert.Single(store.Realms.Realms);
        Assert.Equal(RealmEntry.LocalServerId, store.Realms.Realms[0].Id);
    }

    [Fact]
    public void SelectedRealm_DefaultsToLocal()
    {
        using var tmp = new TempDir();
        var store = new StateStore(tmp.Paths());
        Assert.Equal(RealmEntry.LocalServerId, store.SelectedRealm().Id);
    }

    [Fact]
    public void SelectedRealm_UnknownId_FallsBackToLocal()
    {
        using var tmp = new TempDir();
        var paths = tmp.Paths();
        JsonStore.Save(paths.SettingsFile, new LauncherSettings { LastSelectedRealmId = "deleted-realm" });
        var store = new StateStore(paths);
        Assert.Equal(RealmEntry.LocalServerId, store.SelectedRealm().Id);
    }

    [Fact]
    public void SelectRealm_PersistsSelection()
    {
        using var tmp = new TempDir();
        var paths = tmp.Paths();
        var store = new StateStore(paths);
        store.Realms.Realms.Add(new RealmEntry { Id = "r2", DisplayName = "Two" });
        store.SaveRealms();
        store.SelectRealm("r2");

        Assert.Equal("r2", store.SelectedRealm().Id);
        var again = new StateStore(paths);
        Assert.Equal("r2", again.Settings.LastSelectedRealmId);
        Assert.Equal("r2", again.SelectedRealm().Id);
    }

    [Fact]
    public void Settings_Roundtrip_ThroughStore()
    {
        using var tmp = new TempDir();
        var paths = tmp.Paths();
        var store = new StateStore(paths);
        store.Settings.Language = "de";
        store.Settings.OnboardingCompleted = true;
        store.Settings.Server.MysqlPort = 3399;
        store.SaveSettings();

        var again = new StateStore(paths);
        Assert.Equal("de", again.Settings.Language);
        Assert.True(again.Settings.OnboardingCompleted);
        Assert.Equal(3399, again.Settings.Server.MysqlPort);
    }

    [Fact]
    public void Rollback_SaveAndClear()
    {
        using var tmp = new TempDir();
        var paths = tmp.Paths();
        var store = new StateStore(paths);
        Assert.Null(store.Rollback);

        store.SaveRollback(new RollbackMetadata { BackupId = "b1", FromVersion = "1", ToVersion = "2", Directory = tmp.Path });
        Assert.NotNull(store.Rollback);
        Assert.True(File.Exists(paths.RollbackFile));

        var again = new StateStore(paths);
        Assert.Equal("b1", again.Rollback!.BackupId);

        again.SaveRollback(null);
        Assert.Null(again.Rollback);
        Assert.False(File.Exists(paths.RollbackFile));
    }

    [Fact]
    public void CorruptRealmsFile_Throws_RatherThanSilentlyLosingData()
    {
        using var tmp = new TempDir();
        tmp.File("config/realms.json", "{ this is not json");
        Assert.ThrowsAny<Exception>(() => new StateStore(tmp.Paths()));
    }
}
