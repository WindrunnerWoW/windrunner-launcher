using WindrunnerLauncher.Core.Client;
using WindrunnerLauncher.Core.Downloads;
using WindrunnerLauncher.Core.Localization;
using WindrunnerLauncher.Core.Models;
using WindrunnerLauncher.Core.Mods;
using WindrunnerLauncher.Core.News;
using WindrunnerLauncher.Core.Persistence;
using WindrunnerLauncher.Core.Platform;
using WindrunnerLauncher.Core.Realms;
using WindrunnerLauncher.Core.Server;
using WindrunnerLauncher.Core.Updates;

namespace WindrunnerLauncher.Core;

public sealed class LauncherRuntime : IDisposable
{
    public LauncherPaths Paths { get; }
    public StateStore State { get; }
    public Loc Loc { get; }
    public DownloadManager Downloads { get; }
    public RealmManager Realms { get; }
    public ClientManager Client { get; }
    public ClientPatchUpdater ClientPatches { get; }
    public ModManager Mods { get; }
    public AddonManager Addons { get; }
    public MariaDbManager MariaDb { get; }
    public ServerManager Server { get; }
    public PlayPipeline Play { get; }
    public UpdateManager Updates { get; }
    public NewsFeedService News { get; }
    public BackupRollbackManager Rollback { get; }
    public ServerBackupService Backups { get; }
    public GitHubAuthService GitHub { get; }
    public LinuxRunner Runner { get; }

    public DownloadProgress? LastDownload { get; private set; }
    public event Action? Changed;

    /// <param name="releases">GitHub release client for update checks. Tests pass an offline one so no live release is read.</param>
    public LauncherRuntime(LauncherPaths? paths = null, HttpClient? http = null, GitHubReleases? releases = null)
    {
        Paths = paths ?? LauncherPaths.FromExecutable();
        Paths.EnsureLayout();
        ExtractServerTemplates();
        State = new StateStore(Paths);
        Loc = new Loc();
        Loc.Discover(Paths);
        if (!string.IsNullOrWhiteSpace(State.Settings.Language))
            Loc.Load(State.Settings.Language);

        Downloads = new DownloadManager(http);
        Downloads.Progress += p =>
        {
            LastDownload = p;
            Changed?.Invoke();
        };

        GitHub = new GitHubAuthService(State);
        GitHub.Changed += () => Changed?.Invoke();

        Realms = new RealmManager(State);
        Client = new ClientManager(Paths, State);
        ClientPatches = new ClientPatchUpdater(Paths, Client, Downloads);
        ClientPatches.Changed += () => Changed?.Invoke();
        Mods = new ModManager(Paths, State, Downloads, new ReleaseCatalog());
        Addons = new AddonManager(Paths, Downloads, new ReleaseCatalog());
        Addons.Changed += () => Changed?.Invoke();
        MariaDb = new MariaDbManager(Paths, State);
        Server = new ServerManager(Paths, State, MariaDb, Mods, Downloads);
        Rollback = new BackupRollbackManager(Paths, State);
        var databaseBackup = new MariaDbDatabaseBackup(MariaDb);
        Updates = new UpdateManager(Paths, State, Downloads, Server, Mods, Rollback, releases, databaseBackup: databaseBackup);
        Updates.Changed += () => Changed?.Invoke();
        Backups = new ServerBackupService(Paths, Server, databaseBackup, () => Updates.InstalledServerVersion);
        News = new NewsFeedService();
        Runner = new LinuxRunner(Paths, Downloads);
        Play = new PlayPipeline(Paths, State, Client, Mods, Server, Realms, Runner);
        Server.StateChanged += () => Changed?.Invoke();
        News.Changed += () => Changed?.Invoke();
        Mods.Changed += () => Changed?.Invoke();
        Notify();
    }

    public void Notify() => Changed?.Invoke();

    public HomeStatus BuildHomeStatus()
    {
        var realm = State.SelectedRealm();
        var serverInstalled = Server.IsInstalled;
        var clientReady = Client.IsValid(Client.ClientForRealm(realm));
        var outdatedPatches = clientReady
            ? ClientPatches.PendingFor(realm).Select(update => update.Asset.Name).ToList()
            : [];
        var action = ResolvePlay(realm, serverInstalled, clientReady);
        return new HomeStatus
        {
            Realm = realm,
            ClientReady = clientReady,
            ServerInstalled = serverInstalled,
            ServerState = Server.State,
            EnabledModCount = Mods.EnabledCountFor(realm),
            ClientUpdateRequired = Updates.ClientUpdateRequired || outdatedPatches.Count > 0,
            OutdatedClientPatches = outdatedPatches,
            ServerUpdateAvailable = Updates.ServerUpdateAvailable,
            LauncherUpdateAvailable = Updates.LauncherUpdateAvailable,
            PatchNotes = Updates.PatchNotes,
            Play = action
        };
    }

    public PlayContext ResolvePlay(RealmEntry realm, bool serverInstalled, bool clientReady)
    {
        if (!State.Settings.OnboardingCompleted)
            return new PlayContext { Action = PlayActionKind.Onboarding, ButtonLabel = Loc["onboarding.continue"], Enabled = true };

        if (!clientReady)
            return new PlayContext
            {
                Action = PlayActionKind.Onboarding,
                ButtonLabel = Loc["onboarding.locate"],
                Enabled = true,
                BlockReason = Loc["status.missing"]
            };

        if (Updates.ClientUpdateRequired || ClientPatches.PendingFor(realm).Count > 0)
            return new PlayContext { Action = PlayActionKind.Update, ButtonLabel = Loc["update"], Enabled = true };

        if (realm.Id == RealmEntry.LocalServerId)
        {
            if (!serverInstalled)
                return new PlayContext
                {
                    Action = PlayActionKind.InstallServer,
                    ButtonLabel = Loc["install.server"],
                    Enabled = true
                };
            if (Server.State == ServerLifecycleState.RestartRequired)
                return new PlayContext
                {
                    Action = PlayActionKind.RestartRequired,
                    ButtonLabel = Loc["restart.required"],
                    Enabled = true,
                    BlockReason = Loc["server.restart"]
                };
        }

        return new PlayContext { Action = PlayActionKind.Play, ButtonLabel = Loc["play"], Enabled = true };
    }

    private void ExtractServerTemplates()
    {
        EmbeddedResources.ExtractTo("portable.env", Paths.PortableEnv);
        EmbeddedResources.ExtractTo("my.ini.template", Paths.MyIniTemplate);
    }

    public void Dispose()
    {
        Downloads.Dispose();
        Server.Dispose();
        MariaDb.Dispose();
    }
}
