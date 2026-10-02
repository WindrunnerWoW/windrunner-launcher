namespace WindrunnerLauncher.Core.Models;

public enum ServerLifecycleState
{
    NotInstalled,
    Stopped,
    StartingDatabase,
    StartingAuth,
    InitializingWorld,
    Ready,
    Stopping,
    RestartRequired,
    Updating,
    RollingBack,
    Error
}

public enum PlayActionKind
{
    Onboarding,
    Play,
    Update,
    InstallServer,
    RestartRequired
}

public enum LauncherReadinessKind
{
    NoClient,
    Error,
    UpdateRequired,
    RestartRequired,
    Installing,
    Starting,
    Offline,
    Ready
}

public enum ModKind
{
    Mpq,
    Dll,
    AddOn,
    ExecutablePatch,
    Configuration,
    ZipRoot,
    WdbBlock,
    Glue,
    Dxvk
}

public enum TrustDomain
{
    Launcher,
    Client,
    Server
}

public enum InstallChoice
{
    ClientOnly,
    ClientAndLocalServer
}

public enum DownloadStatus
{
    Idle,
    Running,
    Cancelling,
    RetryWait,
    Succeeded,
    Failed,
    Cancelled
}
