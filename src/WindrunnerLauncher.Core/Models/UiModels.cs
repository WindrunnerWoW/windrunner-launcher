namespace WindrunnerLauncher.Core.Models;

public sealed class UnmanagedItem
{
    public string Name { get; set; } = "";
    public string Path { get; set; } = "";
    public ModKind Kind { get; set; }
}

public sealed class ModListItem
{
    public ManagedAsset Asset { get; set; } = new();
    public bool Enabled { get; set; }
    public bool Installed { get; set; }
    public bool UpdateAvailable { get; set; }
    public bool Unmanaged { get; set; }
}

public sealed class ClientMpqItem
{
    public string FileName { get; set; } = "";
    public bool Required { get; set; }
    public bool Enabled { get; set; }
    public bool Active { get; set; }
}

public sealed class DownloadProgress
{
    public string Id { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public long BytesReceived { get; set; }
    public long? TotalBytes { get; set; }
    public DownloadStatus Status { get; set; }
    public int Attempt { get; set; } = 1;
    public int MaxAttempts { get; set; } = 5;
    public int RetryInSeconds { get; set; }
    public string? Error { get; set; }
    public double? Fraction => TotalBytes is > 0 ? (double)BytesReceived / TotalBytes.Value : null;
}

public readonly record struct SetupStageInfo(string Id, string DisplayName);

public sealed class SetupStage
{
    public string Id { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public bool Completed { get; set; }
    public bool Active { get; set; }
    public string? Detail { get; set; }
    public int Index { get; set; }
    public int Total { get; set; }
    public bool Failed => !Completed && !string.IsNullOrWhiteSpace(Detail);
    public bool HasDetail => !string.IsNullOrWhiteSpace(Detail);
    public bool IsPending => !Completed && !Active && !Failed;
    public bool IsDimmed => !Active && !Failed;
    public string Glyph => Failed ? "✕" : Completed ? "✓" : Active ? "◆" : "○";
}

public sealed class PlayContext
{
    public PlayActionKind Action { get; set; } = PlayActionKind.Play;
    public string ButtonLabel { get; set; } = "Play";
    public bool Enabled { get; set; } = true;
    public string? BlockReason { get; set; }
}

public sealed class HomeStatus
{
    public RealmEntry? Realm { get; set; }
    public bool ClientReady { get; set; }
    public bool ServerInstalled { get; set; }
    public ServerLifecycleState ServerState { get; set; }
    public int EnabledModCount { get; set; }
    public bool ClientUpdateRequired { get; set; }
    /// <summary>Server MPQs on the selected realm's client that are missing or older than the release.</summary>
    public IReadOnlyList<string> OutdatedClientPatches { get; set; } = [];
    public bool ServerUpdateAvailable { get; set; }
    public bool LauncherUpdateAvailable { get; set; }
    public string? PatchNotes { get; set; }
    public PlayContext Play { get; set; } = new();
}

public readonly record struct LauncherReadiness(LauncherReadinessKind Kind, string TextKey, bool IsBusy);

/// <summary>
/// Collapses the many backend lifecycle flags into one Home-footer state.
/// </summary>
public static class LauncherReadinessResolver
{
    public static LauncherReadiness Resolve(HomeStatus status, bool operationInProgress = false)
    {
        var server = status.ServerState;
        var local = status.Realm is null || status.Realm.Id == RealmEntry.LocalServerId;

        if (!status.ClientReady && !operationInProgress)
            return new(LauncherReadinessKind.NoClient, "status.launcher.noclient", false);

        if (server == ServerLifecycleState.Error)
            return new(LauncherReadinessKind.Error, "status.launcher.error", false);

        if (status.ClientUpdateRequired && !operationInProgress)
            return new(LauncherReadinessKind.UpdateRequired, "status.launcher.update", false);

        if (server == ServerLifecycleState.RestartRequired)
            return new(LauncherReadinessKind.RestartRequired, "status.launcher.restart", false);

        if (operationInProgress || server is ServerLifecycleState.Updating or ServerLifecycleState.RollingBack)
        {
            var installing = !status.ClientReady || status.Play.Action == PlayActionKind.InstallServer;
            return new(
                LauncherReadinessKind.Installing,
                installing ? "status.launcher.installing" : "status.launcher.updating",
                true);
        }

        if (server is ServerLifecycleState.StartingDatabase
            or ServerLifecycleState.StartingAuth
            or ServerLifecycleState.InitializingWorld
            or ServerLifecycleState.Stopping)
            return new(LauncherReadinessKind.Starting, "status.launcher.starting", true);

        if (local && server != ServerLifecycleState.Ready)
            return new(LauncherReadinessKind.Offline, "status.launcher.offline", false);

        return new(LauncherReadinessKind.Ready, "status.launcher.ready", false);
    }
}

public sealed class TrustedKey
{
    public string KeyId { get; set; } = "";
    public TrustDomain Domain { get; set; }
    public string PublicKeyB64 { get; set; } = "";
    public bool Retired { get; set; }
}
