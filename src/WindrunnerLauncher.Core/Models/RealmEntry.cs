namespace WindrunnerLauncher.Core.Models;

public sealed class RealmEntry
{
    public const string LocalServerId = "local";

    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string DisplayName { get; set; } = "";
    public string Address { get; set; } = "127.0.0.1";
    public int AuthPort { get; set; } = 3724;
    public string? InGameRealmName { get; set; }
    public string? ClientDirectoryOverride { get; set; }
    public string ClientExecutable { get; set; } = "WoW.exe";
    public bool ClearWdb { get; set; }
    public Dictionary<string, bool> ManagedModState { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, bool> MpqFileState { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    public override string ToString() =>
        string.IsNullOrWhiteSpace(DisplayName) ? Id : DisplayName;
}

public sealed class RealmListState
{
    public List<RealmEntry> Realms { get; set; } = [];
}
