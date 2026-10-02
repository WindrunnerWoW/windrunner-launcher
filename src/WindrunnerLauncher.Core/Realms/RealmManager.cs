using WindrunnerLauncher.Core.Models;
using WindrunnerLauncher.Core.Persistence;

namespace WindrunnerLauncher.Core.Realms;

/// <summary>
/// Owns the realm list. Selecting or editing a realm performs no file operations at all — the Play
/// pipeline is the single point where a realm is materialized into the client directory.
/// </summary>
public sealed class RealmManager
{
    private readonly StateStore _state;

    public event Action? Changed;

    public RealmManager(StateStore state)
    {
        _state = state;
        SyncLocalFromServerSettings();
    }

    public IReadOnlyList<RealmEntry> List() => _state.Realms.Realms.AsReadOnly();

    /// <summary>The undeletable Local Server entry. Recreated by the state store if it goes missing.</summary>
    public RealmEntry Local =>
        _state.Realms.Realms.FirstOrDefault(r => r.Id == RealmEntry.LocalServerId)
        ?? throw new InvalidOperationException("The Local Server realm is missing from the realm list.");

    public RealmEntry Selected => _state.SelectedRealm();

    public RealmEntry? Find(string id) =>
        _state.Realms.Realms.FirstOrDefault(r => string.Equals(r.Id, id, StringComparison.Ordinal));

    public static bool IsLocal(RealmEntry realm) => realm.Id == RealmEntry.LocalServerId;

    public static bool IsDeletable(string id) => id != RealmEntry.LocalServerId;

    /// <summary>Remembers the selection. Deliberately free of side effects.</summary>
    public void Select(string id)
    {
        if (Find(id) is null)
            throw new KeyNotFoundException($"Unknown realm: {id}");
        _state.SelectRealm(id);
        Changed?.Invoke();
    }

    public RealmEntry Add(RealmEntry realm)
    {
        if (string.IsNullOrWhiteSpace(realm.Id) || realm.Id == RealmEntry.LocalServerId)
            realm.Id = Guid.NewGuid().ToString("N");
        if (Find(realm.Id) is not null)
            throw new InvalidOperationException($"A realm with id {realm.Id} already exists.");
        if (string.IsNullOrWhiteSpace(realm.DisplayName))
            realm.DisplayName = realm.Address;

        Normalize(realm);
        _state.Realms.Realms.Add(realm);
        _state.SaveRealms();
        Changed?.Invoke();
        return realm;
    }

    /// <summary>
    /// Applies edits to the stored entry. The Local Server keeps its identity: its display name and
    /// id are fixed, but its in-game realm name, address and mod state remain editable.
    /// </summary>
    public void Update(RealmEntry realm)
    {
        var existing = Find(realm.Id)
                       ?? throw new KeyNotFoundException($"Unknown realm: {realm.Id}");

        Normalize(realm);
        existing.DisplayName = IsLocal(existing) ? existing.DisplayName : realm.DisplayName;
        existing.Address = realm.Address;
        existing.AuthPort = realm.AuthPort;
        existing.InGameRealmName = realm.InGameRealmName;
        existing.ClientDirectoryOverride = realm.ClientDirectoryOverride;
        existing.ClientExecutable = realm.ClientExecutable;
        existing.ClearWdb = realm.ClearWdb;
        if (!ReferenceEquals(existing.ManagedModState, realm.ManagedModState))
        {
            existing.ManagedModState.Clear();
            foreach (var pair in realm.ManagedModState)
                existing.ManagedModState[pair.Key] = pair.Value;
        }
        if (!ReferenceEquals(existing.MpqFileState, realm.MpqFileState))
        {
            existing.MpqFileState.Clear();
            foreach (var pair in realm.MpqFileState)
                existing.MpqFileState[pair.Key] = pair.Value;
        }

        if (IsLocal(existing))
            PushLocalToServerSettings(existing);

        _state.SaveRealms();
        Changed?.Invoke();
    }

    public void Delete(string id)
    {
        if (!IsDeletable(id))
            throw new InvalidOperationException("The Local Server realm cannot be deleted.");

        var realm = Find(id) ?? throw new KeyNotFoundException($"Unknown realm: {id}");
        _state.Realms.Realms.Remove(realm);
        if (_state.Settings.LastSelectedRealmId == id)
            _state.SelectRealm(RealmEntry.LocalServerId);
        _state.SaveRealms();
        Changed?.Invoke();
    }

    /// <summary>Records the desired managed-mod state for a realm. No files move until Play.</summary>
    public void SetModState(string realmId, string assetId, bool enabled)
    {
        var realm = Find(realmId) ?? throw new KeyNotFoundException($"Unknown realm: {realmId}");
        realm.ManagedModState[assetId] = enabled;
        _state.SaveRealms();
        Changed?.Invoke();
    }

    public void ClearModState(string realmId, string assetId)
    {
        var realm = Find(realmId) ?? throw new KeyNotFoundException($"Unknown realm: {realmId}");
        if (realm.ManagedModState.Remove(assetId))
        {
            _state.SaveRealms();
            Changed?.Invoke();
        }
    }

    /// <summary>
    /// The configured server address is the single source of truth: whenever server settings are
    /// saved, the Local Server realm follows them.
    /// </summary>
    public void SyncLocalFromServerSettings()
    {
        var local = _state.Realms.Realms.FirstOrDefault(r => r.Id == RealmEntry.LocalServerId);
        if (local is null)
            return;

        var server = _state.Settings.Server;
        var changed = false;

        if (!string.Equals(local.Address, server.RealmAddress, StringComparison.Ordinal))
        {
            local.Address = server.RealmAddress;
            changed = true;
        }

        if (local.AuthPort != server.AuthPort)
        {
            local.AuthPort = server.AuthPort;
            changed = true;
        }

        if (!string.Equals(local.InGameRealmName, server.RealmName, StringComparison.Ordinal))
        {
            local.InGameRealmName = server.RealmName;
            changed = true;
        }

        if (!changed)
            return;

        _state.SaveRealms();
        Changed?.Invoke();
    }

    private void PushLocalToServerSettings(RealmEntry local)
    {
        var server = _state.Settings.Server;
        var changed = false;

        if (!string.Equals(server.RealmAddress, local.Address, StringComparison.Ordinal))
        {
            server.RealmAddress = local.Address;
            changed = true;
        }

        if (server.AuthPort != local.AuthPort)
        {
            server.AuthPort = local.AuthPort;
            changed = true;
        }

        var name = local.InGameRealmName;
        if (!string.IsNullOrWhiteSpace(name) && !string.Equals(server.RealmName, name, StringComparison.Ordinal))
        {
            server.RealmName = name;
            changed = true;
        }

        if (changed)
            _state.SaveSettings();
    }

    private static void Normalize(RealmEntry realm)
    {
        realm.Address = (realm.Address ?? "").Trim();
        if (realm.Address.Length == 0)
            realm.Address = "127.0.0.1";
        if (realm.AuthPort is <= 0 or > 65535)
            realm.AuthPort = 3724;
        realm.DisplayName = (realm.DisplayName ?? "").Trim();
        if (string.IsNullOrWhiteSpace(realm.ClientExecutable))
            realm.ClientExecutable = "WoW.exe";
        if (string.IsNullOrWhiteSpace(realm.ClientDirectoryOverride))
            realm.ClientDirectoryOverride = null;
    }
}
