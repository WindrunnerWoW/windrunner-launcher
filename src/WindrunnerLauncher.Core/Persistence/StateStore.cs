using WindrunnerLauncher.Core.Models;

namespace WindrunnerLauncher.Core.Persistence;

public sealed class StateStore
{
    private readonly LauncherPaths _paths;
    public LauncherSettings Settings { get; private set; }
    public string GitHubTokenFile => _paths.GitHubTokenFile;
    public RealmListState Realms { get; private set; }
    public RollbackMetadata? Rollback { get; private set; }

    public StateStore(LauncherPaths paths)
    {
        _paths = paths;
        Settings = JsonStore.LoadOrNew(paths.SettingsFile, () => new LauncherSettings());
        Realms = JsonStore.LoadOrNew(paths.RealmsFile, CreateDefaultRealms);
        foreach (var realm in Realms.Realms)
            realm.MpqFileState = new Dictionary<string, bool>(
                realm.MpqFileState ?? new Dictionary<string, bool>(), StringComparer.OrdinalIgnoreCase);
        Rollback = File.Exists(paths.RollbackFile)
            ? JsonStore.LoadOrNew<RollbackMetadata>(paths.RollbackFile, () => new RollbackMetadata())
            : null;
        EnsureLocalServer();
    }

    public void SaveSettings() => JsonStore.Save(_paths.SettingsFile, Settings);
    public void SaveRealms() => JsonStore.Save(_paths.RealmsFile, Realms);

    public void SaveRollback(RollbackMetadata? meta)
    {
        Rollback = meta;
        if (meta is null)
        {
            if (File.Exists(_paths.RollbackFile))
                File.Delete(_paths.RollbackFile);
            return;
        }

        JsonStore.Save(_paths.RollbackFile, meta);
    }

    public RealmEntry SelectedRealm()
    {
        EnsureLocalServer();
        var id = Settings.LastSelectedRealmId;
        return Realms.Realms.FirstOrDefault(r => r.Id == id)
               ?? Realms.Realms.First(r => r.Id == RealmEntry.LocalServerId);
    }

    public void SelectRealm(string id)
    {
        Settings.LastSelectedRealmId = id;
        SaveSettings();
    }

    private void EnsureLocalServer()
    {
        var local = Realms.Realms.FirstOrDefault(r => r.Id == RealmEntry.LocalServerId);
        if (local is null)
        {
            Realms.Realms.Insert(0, new RealmEntry
            {
                Id = RealmEntry.LocalServerId,
                DisplayName = "Windrunner",
                Address = Settings.Server.RealmAddress,
                AuthPort = Settings.Server.AuthPort,
                InGameRealmName = Settings.Server.RealmName,
                ClientExecutable = "WoW.exe"
            });
            SaveRealms();
            return;
        }

        // Migrate the old generated label once, while leaving user-renamed realms alone.
        if (string.Equals(local.DisplayName, "Local Server", StringComparison.Ordinal)
            && string.Equals(local.InGameRealmName, "Windrunner", StringComparison.Ordinal))
        {
            local.DisplayName = "Windrunner";
            SaveRealms();
        }
    }

    private static RealmListState CreateDefaultRealms() => new()
    {
        Realms =
        [
            new RealmEntry
            {
                Id = RealmEntry.LocalServerId,
                DisplayName = "Windrunner",
                Address = "127.0.0.1",
                AuthPort = 3724,
                InGameRealmName = "Windrunner"
            }
        ]
    };
}
