using System.Text.Json.Serialization;

namespace WindrunnerLauncher.Core.Models;

public sealed class LauncherSettings
{
    public string Language { get; set; } = "en";
    public bool ShowBranding { get; set; } = true;

    /// <summary>Hide the launcher to the system tray after starting the game client.</summary>
    public bool MinimizeToTrayWhenClientStarts { get; set; }

    /// <summary>Remove the client's WDB cache before every game launch.</summary>
    public bool ClearWdbOnClientStart { get; set; } = true;

    /// <summary>When off, the news feed is never downloaded and the home artwork fills the page.</summary>
    public bool ShowNews { get; set; } = true;
    public bool LightMode { get; set; }
    public string? HomeBackgroundPath { get; set; }
    public string LastSelectedRealmId { get; set; } = RealmEntry.LocalServerId;
    public bool CheckUpdatesOnStartup { get; set; } = true;
    public bool OnboardingCompleted { get; set; }

    /// <summary>
    /// Set once the local realm has an account. Until then, the first time the server is ready
    /// after a fresh install the launcher offers the create-account dialog.
    /// </summary>
    public bool LocalAccountCreated { get; set; }
    public InstallChoice InstallChoice { get; set; } = InstallChoice.ClientAndLocalServer;
    public string? ClientPath { get; set; }
    public bool ClientIsManagedCopy { get; set; }
    public string? CleanWowExeBackupPath { get; set; }
    public string? IgnoredServerRelease { get; set; }

    /// <summary>Launcher version the user chose to ignore; a newer build still prompts again.</summary>
    public string? IgnoredLauncherVersion { get; set; }
    public string? InstalledServerVersion { get; set; }
    public string? InstalledClientManifestVersion { get; set; }
    public bool HealthySelfUpdate { get; set; } = true;
    public string? PreviousLauncherExe { get; set; }

    /// <summary>DPAPI-protected (current user), base64. Never the raw GitHub token. Windows only.</summary>
    public string? GitHubTokenProtected { get; set; }
    public string? GitHubLogin { get; set; }

    /// <summary>How the Windows client is started on Linux.</summary>
    public LinuxRunnerMode LinuxRunner { get; set; } = LinuxRunnerMode.Auto;

    /// <summary>
    /// Wine binary, Proton script, or umu-run for <see cref="LinuxRunnerMode.Custom"/>.
    /// A Proton script is launched through umu-run when that is installed.
    /// </summary>
    public string? WineRunnerPath { get; set; }

    /// <summary>GitHub "owner/repo" of the Hearthstone module the card art is built from (Mods > Hearthstone Card Art).</summary>
    public string HearthstoneRepository { get; set; } = Hearthstone.HearthstoneModuleSource.DefaultRepository;

    /// <summary>Branch of <see cref="HearthstoneRepository"/> the card art follows.</summary>
    public string HearthstoneBranch { get; set; } = Hearthstone.HearthstoneModuleSource.DefaultBranch;

    public ServerFriendlySettings Server { get; set; } = new();
    public VanillaTweaksSettings VanillaTweaks { get; set; } = new();
}

public enum LinuxRunnerMode
{
    /// <summary>System Wine when it is installed, otherwise Proton.</summary>
    Auto,

    /// <summary>System Wine: <c>WINE</c>, then <c>wine</c>, then <c>wine64</c>.</summary>
    Wine,

    /// <summary>GE-Proton through umu-run. The launcher fetches umu-run if it is missing.</summary>
    Proton,

    /// <summary>The runner in <see cref="LauncherSettings.WineRunnerPath"/>.</summary>
    Custom
}

public sealed class ServerFriendlySettings
{
    public string RealmName { get; set; } = "Windrunner";
    public string RealmAddress { get; set; } = "127.0.0.1";
    public int AuthPort { get; set; } = 3724;
    public int WorldPort { get; set; } = 8090;
    public int MysqlPort { get; set; } = 3307;
    public int MinRandomBots { get; set; } = 20;
    public int MaxRandomBots { get; set; } = 20;
    public bool StartServerWithClient { get; set; } = true;
}

public sealed class VanillaTweaksSettings
{
    public bool UseRecommendedPreset { get; set; } = true;
    public bool LargeAddressAware { get; set; } = true;
    public double FieldOfViewRadians { get; set; } = 1.925;
    public int CameraDistanceMax { get; set; } = 100;
    public int FarClip { get; set; } = 777;
    public bool AlwaysAutoLoot { get; set; } = true;
    public bool SoundInBackground { get; set; } = false;
    public bool NameplateRangeTbc { get; set; } = true;
    public bool MoreSoundChannels { get; set; } = true;
}
