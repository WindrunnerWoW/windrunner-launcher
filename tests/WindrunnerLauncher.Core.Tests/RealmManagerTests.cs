using WindrunnerLauncher.Core.Persistence;
using WindrunnerLauncher.Core.Realms;

namespace WindrunnerLauncher.Core.Tests;

public class RealmManagerTests
{
    private static (TempDir Tmp, StateStore State, RealmManager Realms) Create()
    {
        var tmp = new TempDir();
        var state = new StateStore(tmp.Paths());
        return (tmp, state, new RealmManager(state));
    }

    [Fact]
    public void Local_AlwaysPresent_AndSelectedByDefault()
    {
        var (tmp, _, realms) = Create();
        using var _ = tmp;
        Assert.Equal(RealmEntry.LocalServerId, realms.Local.Id);
        Assert.Equal(RealmEntry.LocalServerId, realms.Selected.Id);
        Assert.True(RealmManager.IsLocal(realms.Local));
        Assert.False(RealmManager.IsDeletable(RealmEntry.LocalServerId));
        Assert.True(RealmManager.IsDeletable("anything-else"));
    }

    [Fact]
    public void Add_AssignsId_NormalizesAndPersists()
    {
        var (tmp, state, realms) = Create();
        using var _ = tmp;
        var changed = 0;
        realms.Changed += () => changed++;

        var added = realms.Add(new RealmEntry { Id = "", DisplayName = "  Turtle  ", Address = " logon.example.org ", AuthPort = 70000, ClientExecutable = "", ClientDirectoryOverride = " " });

        Assert.False(string.IsNullOrWhiteSpace(added.Id));
        Assert.NotEqual(RealmEntry.LocalServerId, added.Id);
        Assert.Equal("Turtle", added.DisplayName);
        Assert.Equal("logon.example.org", added.Address);
        Assert.Equal(3724, added.AuthPort);
        Assert.Equal("WoW.exe", added.ClientExecutable);
        Assert.Null(added.ClientDirectoryOverride);
        Assert.Equal(1, changed);
        Assert.Equal(2, realms.List().Count);

        var reloaded = new StateStore(tmp.Paths());
        Assert.Contains(reloaded.Realms.Realms, r => r.Id == added.Id);
        Assert.Equal(state.Realms.Realms.Count, reloaded.Realms.Realms.Count);
    }

    [Fact]
    public void Add_WithLocalId_GetsFreshId()
    {
        var (tmp, _, realms) = Create();
        using var _ = tmp;
        var added = realms.Add(new RealmEntry { Id = RealmEntry.LocalServerId, Address = "x" });
        Assert.NotEqual(RealmEntry.LocalServerId, added.Id);
        Assert.Equal(2, realms.List().Count);
    }

    [Fact]
    public void Add_DisplayNameDefaultsToAddress()
    {
        var (tmp, _, realms) = Create();
        using var _ = tmp;
        var added = realms.Add(new RealmEntry { Address = "logon.example.org" });
        Assert.Equal("logon.example.org", added.DisplayName);
    }

    [Fact]
    public void Add_DuplicateId_Throws()
    {
        var (tmp, _, realms) = Create();
        using var _ = tmp;
        realms.Add(new RealmEntry { Id = "dup", Address = "a" });
        Assert.Throws<InvalidOperationException>(() => realms.Add(new RealmEntry { Id = "dup", Address = "b" }));
    }

    [Fact]
    public void Select_PersistsAndRaisesChanged_UnknownThrows()
    {
        var (tmp, state, realms) = Create();
        using var _ = tmp;
        var added = realms.Add(new RealmEntry { Address = "a" });
        var changed = 0;
        realms.Changed += () => changed++;

        realms.Select(added.Id);
        Assert.Equal(added.Id, realms.Selected.Id);
        Assert.Equal(added.Id, state.Settings.LastSelectedRealmId);
        Assert.Equal(1, changed);
        Assert.Throws<KeyNotFoundException>(() => realms.Select("ghost"));

        Assert.Equal(added.Id, new StateStore(tmp.Paths()).Settings.LastSelectedRealmId);
    }

    [Fact]
    public void Select_PerformsNoFileOperationsOutsideConfig()
    {
        var (tmp, _, realms) = Create();
        using var _ = tmp;
        var added = realms.Add(new RealmEntry { Address = "a" });
        realms.Select(added.Id);
        var files = Directory.EnumerateFiles(tmp.Path, "*", SearchOption.AllDirectories)
            .Select(f => Path.GetRelativePath(tmp.Path, f))
            .ToList();
        Assert.All(files, f => Assert.StartsWith("config", f));
    }

    [Fact]
    public void Delete_RemovesRealm_AndResetsSelectionToLocal()
    {
        var (tmp, state, realms) = Create();
        using var _ = tmp;
        var added = realms.Add(new RealmEntry { Address = "a" });
        realms.Select(added.Id);

        realms.Delete(added.Id);

        Assert.Null(realms.Find(added.Id));
        Assert.Single(realms.List());
        Assert.Equal(RealmEntry.LocalServerId, state.Settings.LastSelectedRealmId);
        Assert.Equal(RealmEntry.LocalServerId, realms.Selected.Id);
    }

    [Fact]
    public void Delete_Local_Throws()
    {
        var (tmp, _, realms) = Create();
        using var _ = tmp;
        Assert.Throws<InvalidOperationException>(() => realms.Delete(RealmEntry.LocalServerId));
        Assert.NotNull(realms.Local);
    }

    [Fact]
    public void Delete_Unknown_Throws()
    {
        var (tmp, _, realms) = Create();
        using var _ = tmp;
        Assert.Throws<KeyNotFoundException>(() => realms.Delete("ghost"));
    }

    [Fact]
    public void Update_EditsRemoteRealm()
    {
        var (tmp, _, realms) = Create();
        using var _ = tmp;
        var added = realms.Add(new RealmEntry { Address = "a", DisplayName = "A" });

        realms.Update(new RealmEntry
        {
            Id = added.Id,
            DisplayName = "B",
            Address = "b.example.org",
            AuthPort = 3800,
            InGameRealmName = "Bee",
            ClientExecutable = "WoW_tweaked.exe",
            ClearWdb = true,
            ManagedModState = new Dictionary<string, bool> { ["hd"] = true },
            MpqFileState = new Dictionary<string, bool> { ["extra.mpq"] = false }
        });

        var stored = realms.Find(added.Id)!;
        Assert.Equal("B", stored.DisplayName);
        Assert.Equal("b.example.org", stored.Address);
        Assert.Equal(3800, stored.AuthPort);
        Assert.Equal("Bee", stored.InGameRealmName);
        Assert.Equal("WoW_tweaked.exe", stored.ClientExecutable);
        Assert.True(stored.ClearWdb);
        Assert.True(stored.ManagedModState["hd"]);
        Assert.False(stored.MpqFileState["extra.mpq"]);
    }

    [Fact]
    public void Update_Local_KeepsDisplayName_AndPushesToServerSettings()
    {
        var (tmp, state, realms) = Create();
        using var _ = tmp;

        realms.Update(new RealmEntry
        {
            Id = RealmEntry.LocalServerId,
            DisplayName = "Renamed",
            Address = "192.168.1.20",
            AuthPort = 3730,
            InGameRealmName = "Homebrew"
        });

        var local = realms.Local;
        Assert.Equal("Windrunner", local.DisplayName);
        Assert.Equal("192.168.1.20", local.Address);
        Assert.Equal(3730, local.AuthPort);
        Assert.Equal("Homebrew", local.InGameRealmName);

        Assert.Equal("192.168.1.20", state.Settings.Server.RealmAddress);
        Assert.Equal(3730, state.Settings.Server.AuthPort);
        Assert.Equal("Homebrew", state.Settings.Server.RealmName);

        var reloaded = new StateStore(tmp.Paths());
        Assert.Equal("Homebrew", reloaded.Settings.Server.RealmName);
    }

    [Fact]
    public void Update_Unknown_Throws()
    {
        var (tmp, _, realms) = Create();
        using var _ = tmp;
        Assert.Throws<KeyNotFoundException>(() => realms.Update(new RealmEntry { Id = "ghost" }));
    }

    [Fact]
    public void SyncLocalFromServerSettings_FollowsServerSettings()
    {
        var (tmp, state, realms) = Create();
        using var _ = tmp;
        state.Settings.Server.RealmAddress = "10.1.1.1";
        state.Settings.Server.AuthPort = 3777;
        state.Settings.Server.RealmName = "Synced";
        var changed = 0;
        realms.Changed += () => changed++;

        realms.SyncLocalFromServerSettings();

        Assert.Equal("10.1.1.1", realms.Local.Address);
        Assert.Equal(3777, realms.Local.AuthPort);
        Assert.Equal("Synced", realms.Local.InGameRealmName);
        Assert.Equal(1, changed);

        realms.SyncLocalFromServerSettings();
        Assert.Equal(1, changed);
    }

    [Fact]
    public void Constructor_SyncsLocalFromPersistedServerSettings()
    {
        using var tmp = new TempDir();
        var paths = tmp.Paths();
        JsonStore.Save(paths.SettingsFile, new LauncherSettings { Server = new ServerFriendlySettings { RealmAddress = "172.16.0.1", AuthPort = 3801 } });
        JsonStore.Save(paths.RealmsFile, new RealmListState
        {
            Realms = [new RealmEntry { Id = RealmEntry.LocalServerId, DisplayName = "Local Server", Address = "127.0.0.1", AuthPort = 3724 }]
        });

        var realms = new RealmManager(new StateStore(paths));
        Assert.Equal("172.16.0.1", realms.Local.Address);
        Assert.Equal(3801, realms.Local.AuthPort);
    }

    [Fact]
    public void SetModState_And_ClearModState()
    {
        var (tmp, _, realms) = Create();
        using var _ = tmp;
        var changed = 0;
        realms.Changed += () => changed++;

        realms.SetModState(RealmEntry.LocalServerId, "vanillafixes", true);
        Assert.True(realms.Local.ManagedModState["vanillafixes"]);
        Assert.Equal(1, changed);

        realms.SetModState(RealmEntry.LocalServerId, "VANILLAFIXES", false);
        Assert.False(realms.Local.ManagedModState["vanillafixes"]);
        Assert.Single(realms.Local.ManagedModState);

        realms.ClearModState(RealmEntry.LocalServerId, "vanillafixes");
        Assert.Empty(realms.Local.ManagedModState);
        Assert.Equal(3, changed);

        realms.ClearModState(RealmEntry.LocalServerId, "vanillafixes");
        Assert.Equal(3, changed);

        Assert.Throws<KeyNotFoundException>(() => realms.SetModState("ghost", "x", true));

        var reloaded = new StateStore(tmp.Paths());
        Assert.Empty(reloaded.Realms.Realms[0].ManagedModState);
    }

    [Fact]
    public void ModState_SurvivesRoundtrip_CaseInsensitive()
    {
        var (tmp, _, realms) = Create();
        using var _ = tmp;
        realms.SetModState(RealmEntry.LocalServerId, "HD-Patch", true);
        realms.SetModState(RealmEntry.LocalServerId, "hd-patch", true);
        Assert.Single(realms.Local.ManagedModState);

        var reloaded = new StateStore(tmp.Paths());
        var pair = Assert.Single(reloaded.Realms.Realms[0].ManagedModState);
        Assert.Equal("HD-Patch", pair.Key);
        Assert.True(pair.Value);
    }
}
