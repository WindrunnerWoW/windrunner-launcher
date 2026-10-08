using System.Collections.ObjectModel;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using WindrunnerLauncher.Core;
using WindrunnerLauncher.Core.Models;
using WindrunnerLauncher.Core.Updates;

namespace WindrunnerLauncher.App.ViewModels;

public sealed partial class MainViewModel : ObservableObject, IDisposable
{
    private readonly LauncherRuntime _runtime;
    private bool _disposed;
    private int _refreshQueued;
    private int _logRefreshQueued;

    public MainViewModel(LauncherRuntime runtime)
    {
        _runtime = runtime;
        Texts = new LocalizedStrings(runtime.Loc);
        Home = new HomeViewModel(this);
        Mods = new ModsViewModel(this);
        Server = new ServerViewModel(this);
        Settings = new SettingsViewModel(this);
        Realms = new ObservableCollection<RealmEntry>(runtime.State.Realms.Realms);
        _currentPage = Home;

        runtime.Changed += RuntimeChanged;
        runtime.Server.StateChanged += RuntimeChanged;
        runtime.Server.OutputReceived += ServerOutputReceived;
        runtime.Loc.Changed += LanguageChanged;
        Refresh();
        if (runtime.State.Settings.CheckUpdatesOnStartup)
            _ = CheckUpdatesInBackgroundAsync();
        _ = RefreshNewsInBackgroundAsync();
        // The server patches are needed to connect, so they are checked even with update checks off.
        _ = CheckClientPatchesAsync(runtime.State.SelectedRealm(), fetch: true);
    }

    public event Action? OnboardingRequested;
    public event Action<string, string>? ErrorRequested;
    public event Action<RealmEntry?>? RealmEditorRequested;
    public event Action? ServerUpdateRequested;
    public event Action? ServerRollbackRequested;

    internal LauncherRuntime Runtime => _runtime;
    public LocalizedStrings Texts { get; }
    public HomeViewModel Home { get; }
    public ModsViewModel Mods { get; }
    public ServerViewModel Server { get; }
    public SettingsViewModel Settings { get; }
    public ObservableCollection<RealmEntry> Realms { get; }

    [ObservableProperty]
    private object _currentPage;

    [ObservableProperty]
    private RealmEntry? _selectedRealm;

    [ObservableProperty]
    private HomeStatus _status = new();

    [ObservableProperty]
    private bool _isBusy;

    [ObservableProperty]
    private double _downloadProgress;

    [ObservableProperty]
    private bool _downloadIndeterminate;

    [ObservableProperty]
    private string _downloadText = "";

    [ObservableProperty]
    private bool _hasDownload;

    public string Title => Texts["app.title"];
    public bool ShowBranding => _runtime.State.Settings.ShowBranding;
    public string PlayLabel => Status.Play.ButtonLabel;
    public string PlayCaption => Status.Play.Action switch
    {
        PlayActionKind.Play => Texts["play"].ToUpperInvariant(),
        PlayActionKind.Update => Texts["update"].ToUpperInvariant(),
        PlayActionKind.InstallServer => Texts["install.server"].ToUpperInvariant(),
        PlayActionKind.RestartRequired => Texts["restart.required.short"].ToUpperInvariant(),
        PlayActionKind.Onboarding when !Status.ClientReady => Texts["onboarding.setup"].ToUpperInvariant(),
        _ => Texts["onboarding.continue"].ToUpperInvariant()
    };
    public string? PlayHint => Status.Play.BlockReason;
    public bool CanPlay => Status.Play.Enabled && !IsBusy;
    public string RealmCaption => SelectedRealm?.DisplayName ?? Texts["realm.local"];
    public string VersionLabel => Texts.Format("app.version", _runtime.Updates.CurrentLauncherVersion);
    public string ServerStateText => ServerStateLabel(Status.ServerState);
    public string ClientStateText => !Status.ClientReady
        ? Texts["status.missing"]
        : ClientHasPatchUpdate
            ? Texts.Format("status.client.patches", OutdatedPatchList)
            : Texts["status.ready"];
    public bool ClientHasPatchUpdate => Status.ClientReady && Status.OutdatedClientPatches.Count > 0;
    public string OutdatedPatchList => string.Join(", ", Status.OutdatedClientPatches);
    public string ModsStateText => ModsHasWarning
        ? Texts.Format("status.mods.missing", MissingModCount)
        : Texts["status.ready"];
    public string UpdateStateText => UpdatesIsWarning ? Texts["status.outdated"] : Texts["status.uptodate"];
    public string ServerFooterText => ServerIsOnline ? Texts["status.ready"] : ServerStateText;
    public string ModsFooterText => ModsHasWarning
        ? Texts.Format("status.mods.missing.short", MissingModCount)
        : Texts["status.enabled"];
    public string UpdateFooterText => UpdatesIsWarning ? Texts["status.outdated"] : Texts["status.none"];
    public string ClientChipText => $"{Texts["status.client"]} · {ClientStateText}";
    public string ServerChipText => $"{Texts["status.server"]} · {ServerStateText}";
    public string ModsChipText => $"{Texts["status.mods"]} · {ModsStateText}";
    public string UpdateChipText => $"{Texts["status.updates"]} · {UpdateStateText}";
    public bool ClientIsReady => Status.ClientReady;
    public bool ClientIsMissing => !Status.ClientReady;
    public bool ServerIsOnline => Status.ServerState == ServerLifecycleState.Ready;
    public bool ServerIsWarning => Status.ServerState is
        ServerLifecycleState.StartingDatabase or
        ServerLifecycleState.StartingAuth or
        ServerLifecycleState.InitializingWorld or
        ServerLifecycleState.Stopping or
        ServerLifecycleState.RestartRequired or
        ServerLifecycleState.Updating or
        ServerLifecycleState.RollingBack;
    public bool ServerIsUnavailable => !ServerIsOnline && !ServerIsWarning;
    public int MissingModCount
    {
        get
        {
            var realm = Status.Realm ?? SelectedRealm;
            if (realm is null)
                return 0;
            return _runtime.Mods.ListItems(realm).Count(item =>
                !item.Unmanaged && item.Enabled && !item.Installed);
        }
    }
    public bool ModsHasWarning => MissingModCount > 0;
    public bool ModsIsReady => !ModsHasWarning;
    public bool UpdatesIsWarning => Status.ClientUpdateRequired || Status.ServerUpdateAvailable || Status.LauncherUpdateAvailable;
    public bool UpdatesIsReady => !UpdatesIsWarning;
    public LauncherReadiness LauncherReadiness { get; private set; } =
        new(LauncherReadinessKind.Offline, "status.launcher.offline", false);
    public string LauncherStatusText => Texts[LauncherReadiness.TextKey];
    public string? LauncherStatusDetail => LauncherReadiness.Kind switch
    {
        LauncherReadinessKind.Ready => Texts["home.status.ready.detail"],
        LauncherReadinessKind.Installing when HasDownload && !string.IsNullOrWhiteSpace(DownloadText) => DownloadText,
        LauncherReadinessKind.Installing when Server.IsSetupInProgress => Server.SetupProgressCaption,
        _ => null
    };
    public bool HasLauncherStatusDetail => !string.IsNullOrWhiteSpace(LauncherStatusDetail);
    public bool LauncherStatusIsOk => LauncherReadiness.Kind == LauncherReadinessKind.Ready;
    public bool LauncherStatusIsWarn => LauncherReadiness.Kind is
        LauncherReadinessKind.UpdateRequired or
        LauncherReadinessKind.RestartRequired or
        LauncherReadinessKind.Installing or
        LauncherReadinessKind.Starting;
    public bool LauncherStatusIsBad => LauncherReadiness.Kind is
        LauncherReadinessKind.NoClient or
        LauncherReadinessKind.Error;
    public bool LauncherStatusIsMuted => LauncherReadiness.Kind == LauncherReadinessKind.Offline;
    public bool LauncherStatusIsBusy => LauncherReadiness.IsBusy;
    public bool HasProgressBar => HasDownload || Server.IsSetupInProgress;
    public double ProgressBarValue => HasDownload ? DownloadProgress : Server.SetupProgressFraction;
    public bool ProgressBarIndeterminate => HasDownload && DownloadIndeterminate;
    public string ProgressBarCaption => HasDownload ? DownloadText : Server.SetupProgressCaption;

    public bool ServerUpdateAvailable => Status.ServerUpdateAvailable;
    /// <summary>The footer hint yields to the progress bar and to any running operation.</summary>
    public bool ShowServerUpdateHint => ServerUpdateAvailable && !HasProgressBar && !IsBusy;
    public string? InstalledServerVersion => _runtime.Updates.InstalledServerVersion;
    public string? LatestServerVersion => _runtime.Updates.ServerReleaseChecked ? _runtime.Updates.LatestServerRelease : null;
    public string ServerReleaseNotes => _runtime.Updates.LatestServerNotes?.Trim() is { Length: > 0 } notes
        ? notes
        : Texts["server.update.dialog.nonotes"];
    public string ServerUpdateVersions
    {
        get
        {
            var from = InstalledServerVersion ?? Texts["server.update.unknown"];
            var to = LatestServerVersion ?? Texts["server.update.unknown"];
            return _runtime.Updates.LatestServerDownloadBytes is { } bytes
                ? Texts.Format("server.update.versions.size", from, to, FormatBytes(bytes))
                : Texts.Format("server.update.versions", from, to);
        }
    }

    [RelayCommand]
    private void ShowServerUpdate()
    {
        // Read the runtime, not Status: un-skipping a release updates it before the next Refresh.
        if (_runtime.Updates.ServerUpdateAvailable && !IsBusy)
            ServerUpdateRequested?.Invoke();
    }

    [RelayCommand]
    private void SkipServerUpdate() => _runtime.Updates.IgnoreServerVersion();

    public void RequestServerRollback()
    {
        if (_runtime.Updates.HasServerRollback && !IsBusy)
            ServerRollbackRequested?.Invoke();
    }

    public Task UpdateServerConfirmedAsync() => RunGuardedAsync(async () =>
    {
        Server.BeginSetupProgress(ServerUpdateStages.Update, "server.update.complete");
        try
        {
            await _runtime.Updates.UpdateServerAsync(Server.SetupProgress);
        }
        finally
        {
            Server.EndSetupProgress();
        }
    });

    public Task RollbackServerConfirmedAsync() => RunGuardedAsync(async () =>
    {
        Server.BeginSetupProgress(ServerUpdateStages.Rollback, "server.update.rollback.complete");
        try
        {
            await _runtime.Updates.RollbackServerAsync(Server.SetupProgress);
        }
        finally
        {
            Server.EndSetupProgress();
        }
    });

    internal void RaiseSetupProgress()
    {
        OnPropertyChanged(nameof(ShowServerUpdateHint));
        OnPropertyChanged(nameof(HasProgressBar));
        OnPropertyChanged(nameof(ProgressBarValue));
        OnPropertyChanged(nameof(ProgressBarIndeterminate));
        OnPropertyChanged(nameof(ProgressBarCaption));
        OnPropertyChanged(nameof(LauncherStatusDetail));
        OnPropertyChanged(nameof(HasLauncherStatusDetail));
    }

    public bool IsHomeSelected => ReferenceEquals(CurrentPage, Home);
    public bool IsModsSelected => ReferenceEquals(CurrentPage, Mods);
    public bool IsServerSelected => ReferenceEquals(CurrentPage, Server);
    public bool IsSettingsSelected => ReferenceEquals(CurrentPage, Settings);
    public bool IsServerRunning => Status.ServerState is not
        (ServerLifecycleState.NotInstalled or ServerLifecycleState.Stopped or ServerLifecycleState.Error);

    partial void OnSelectedRealmChanged(RealmEntry? value)
    {
        if (value is null || value.Id == _runtime.State.Settings.LastSelectedRealmId)
            return;

        _runtime.Realms.Select(value.Id);
        _runtime.Notify();
        _ = CheckClientPatchesAsync(value, fetch: false);
    }

    partial void OnIsBusyChanged(bool value)
    {
        PlayCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(CanPlay));
        OnPropertyChanged(nameof(ShowServerUpdateHint));
    }

    [RelayCommand]
    private void Navigate(string? page)
    {
        CurrentPage = page switch
        {
            "mods" => Mods,
            "server" => Server,
            "settings" => Settings,
            _ => Home
        };
        RaiseNavigationProperties();
    }

    [RelayCommand(CanExecute = nameof(CanPlay))]
    private async Task PlayAsync()
    {
        var action = Status.Play.Action;
        if (action == PlayActionKind.Onboarding)
        {
            OnboardingRequested?.Invoke();
            return;
        }

        await RunGuardedAsync(async () =>
        {
            switch (action)
            {
                case PlayActionKind.Play:
                    await _runtime.Play.ExecuteAsync();
                    break;
                case PlayActionKind.Update:
                    var realm = _runtime.State.SelectedRealm();
                    await _runtime.ClientPatches.ApplyAsync(realm);
                    if (_runtime.Updates.ClientUpdateRequired)
                        await _runtime.Updates.UpdateClientAsync();
                    break;
                case PlayActionKind.InstallServer:
                    Server.BeginSetupProgress();
                    try
                    {
                        await _runtime.Server.SetupAsync(Server.SetupProgress);
                    }
                    finally
                    {
                        Server.EndSetupProgress();
                    }
                    break;
                case PlayActionKind.RestartRequired:
                    await _runtime.Server.RestartAsync();
                    break;
            }
        });
    }

    [RelayCommand]
    private void AddRealm() => RealmEditorRequested?.Invoke(null);

    [RelayCommand]
    private void EditRealm() => RealmEditorRequested?.Invoke(SelectedRealm);

    public async Task<bool> RunGuardedAsync(Func<Task> action)
    {
        if (IsBusy)
            return false;

        IsBusy = true;
        try
        {
            await action();
            return true;
        }
        catch (OperationCanceledException)
        {
            // User cancellation is not an error.
            return false;
        }
        catch (Exception ex)
        {
            ErrorRequested?.Invoke(Texts["error.generic"], ex.Message);
            return false;
        }
        finally
        {
            IsBusy = false;
            _runtime.Notify();
        }
    }

    public void RequestError(string title, string detail) => ErrorRequested?.Invoke(title, detail);

    public void CompleteOnboarding()
    {
        _runtime.Notify();
        // The startup check saw no client yet; re-check now that one exists.
        _ = CheckClientPatchesAsync(_runtime.State.SelectedRealm(), fetch: true);
    }

    public void Refresh()
    {
        if (_disposed)
            return;
        Status = _runtime.BuildHomeStatus();
        SelectedRealm = Realms.FirstOrDefault(x => x.Id == Status.Realm?.Id) ?? Realms.FirstOrDefault();

        var download = _runtime.LastDownload;
        HasDownload = download is not null &&
                      download.Status is DownloadStatus.Running or DownloadStatus.RetryWait or DownloadStatus.Failed;
        DownloadProgress = download?.Fraction ?? 0;
        DownloadIndeterminate = download is { Status: DownloadStatus.Running, Fraction: null };
        DownloadText = download switch
        {
            { Status: DownloadStatus.RetryWait } d =>
                Texts.Format("download.retrying", d.RetryInSeconds, d.Attempt, d.MaxAttempts),
            { Status: DownloadStatus.Failed } d => d.Error ?? Texts.Format("download.failed", d.MaxAttempts),
            { Status: DownloadStatus.Running } d when d.TotalBytes is > 0 =>
                $"{d.DisplayName} · {FormatBytes(d.BytesReceived)} / {FormatBytes(d.TotalBytes.Value)}",
            { Status: DownloadStatus.Running } d => d.DisplayName,
            _ => ""
        };
        LauncherReadiness = LauncherReadinessResolver.Resolve(
            Status,
            download is { Status: DownloadStatus.Running or DownloadStatus.RetryWait });

        OnPropertyChanged(nameof(Title));
        OnPropertyChanged(nameof(ShowBranding));
        OnPropertyChanged(nameof(PlayLabel));
        OnPropertyChanged(nameof(PlayCaption));
        OnPropertyChanged(nameof(PlayHint));
        OnPropertyChanged(nameof(CanPlay));
        OnPropertyChanged(nameof(RealmCaption));
        OnPropertyChanged(nameof(VersionLabel));
        OnPropertyChanged(nameof(ServerStateText));
        OnPropertyChanged(nameof(ClientStateText));
        OnPropertyChanged(nameof(ModsStateText));
        OnPropertyChanged(nameof(UpdateStateText));
        OnPropertyChanged(nameof(ServerFooterText));
        OnPropertyChanged(nameof(ModsFooterText));
        OnPropertyChanged(nameof(UpdateFooterText));
        OnPropertyChanged(nameof(ClientChipText));
        OnPropertyChanged(nameof(ServerChipText));
        OnPropertyChanged(nameof(ModsChipText));
        OnPropertyChanged(nameof(UpdateChipText));
        OnPropertyChanged(nameof(ClientIsReady));
        OnPropertyChanged(nameof(ClientIsMissing));
        OnPropertyChanged(nameof(ClientHasPatchUpdate));
        OnPropertyChanged(nameof(OutdatedPatchList));
        OnPropertyChanged(nameof(ServerIsOnline));
        OnPropertyChanged(nameof(ServerIsWarning));
        OnPropertyChanged(nameof(ServerIsUnavailable));
        OnPropertyChanged(nameof(MissingModCount));
        OnPropertyChanged(nameof(ModsHasWarning));
        OnPropertyChanged(nameof(ModsIsReady));
        OnPropertyChanged(nameof(UpdatesIsWarning));
        OnPropertyChanged(nameof(UpdatesIsReady));
        OnPropertyChanged(nameof(IsServerRunning));
        OnPropertyChanged(nameof(LauncherReadiness));
        OnPropertyChanged(nameof(LauncherStatusText));
        OnPropertyChanged(nameof(LauncherStatusDetail));
        OnPropertyChanged(nameof(HasLauncherStatusDetail));
        OnPropertyChanged(nameof(LauncherStatusIsOk));
        OnPropertyChanged(nameof(LauncherStatusIsWarn));
        OnPropertyChanged(nameof(LauncherStatusIsBad));
        OnPropertyChanged(nameof(LauncherStatusIsMuted));
        OnPropertyChanged(nameof(LauncherStatusIsBusy));
        OnPropertyChanged(nameof(HasProgressBar));
        OnPropertyChanged(nameof(ProgressBarValue));
        OnPropertyChanged(nameof(ProgressBarIndeterminate));
        OnPropertyChanged(nameof(ProgressBarCaption));
        OnPropertyChanged(nameof(ServerUpdateAvailable));
        OnPropertyChanged(nameof(ShowServerUpdateHint));
        OnPropertyChanged(nameof(InstalledServerVersion));
        OnPropertyChanged(nameof(LatestServerVersion));
        OnPropertyChanged(nameof(ServerReleaseNotes));
        OnPropertyChanged(nameof(ServerUpdateVersions));
        PlayCommand.NotifyCanExecuteChanged();
        Home.Refresh();
        Mods.Refresh();
        Server.Refresh();
        Settings.Refresh();
    }

    private async Task CheckUpdatesInBackgroundAsync()
    {
        try
        {
            await _runtime.Updates.CheckAsync().ConfigureAwait(false);
        }
        catch
        {
            // Update checks are best-effort; GitHub being unreachable must not block the UI.
        }

        Dispatcher.UIThread.Post(Refresh);
    }

    /// <summary>
    /// Compares the realm's client against the published server MPQs. Only reports: outdated
    /// patches turn Play into Update, and nothing is downloaded until the user clicks it.
    /// </summary>
    private async Task CheckClientPatchesAsync(RealmEntry realm, bool fetch)
    {
        try
        {
            await _runtime.ClientPatches.CheckAsync(realm, fetch).ConfigureAwait(false);
        }
        catch
        {
            // Offline or rate-limited; Play keeps working with the patches already installed.
        }

        Dispatcher.UIThread.Post(Refresh);
    }

    /// <summary>Fetches the feed, unless the user turned news off.</summary>
    internal void RefreshNews() => _ = RefreshNewsInBackgroundAsync();

    private async Task RefreshNewsInBackgroundAsync()
    {
        if (!_runtime.State.Settings.ShowNews)
            return;
        await _runtime.News.RefreshAsync().ConfigureAwait(false);
        Dispatcher.UIThread.Post(Refresh);
    }

    /// <summary>
    /// Runtime events arrive from background threads, often in bursts (download progress, server
    /// output). Only one refresh is ever queued; it reads the latest state when it runs.
    /// </summary>
    private void RuntimeChanged()
    {
        if (Interlocked.Exchange(ref _refreshQueued, 1) == 1)
            return;
        Dispatcher.UIThread.Post(() =>
        {
            Volatile.Write(ref _refreshQueued, 0);
            Refresh();
        }, DispatcherPriority.Background);
    }
    /// <summary>
    /// mangosd prints thousands of lines at startup; refreshing per line floods the UI thread.
    /// Coalesce output into at most one console refresh every 250 ms.
    /// </summary>
    private void ServerOutputReceived(string _)
    {
        if (Interlocked.Exchange(ref _logRefreshQueued, 1) == 1)
            return;
        Dispatcher.UIThread.Post(() => DispatcherTimer.RunOnce(() =>
        {
            Volatile.Write(ref _logRefreshQueued, 0);
            Server.RefreshLogs();
        }, TimeSpan.FromMilliseconds(250), DispatcherPriority.Background), DispatcherPriority.Background);
    }

    private void LanguageChanged() => Dispatcher.UIThread.Post(() =>
    {
        Texts.Refresh();
        Refresh();
    });

    private void RaiseNavigationProperties()
    {
        OnPropertyChanged(nameof(IsHomeSelected));
        OnPropertyChanged(nameof(IsModsSelected));
        OnPropertyChanged(nameof(IsServerSelected));
        OnPropertyChanged(nameof(IsSettingsSelected));
    }

    private string ServerStateLabel(ServerLifecycleState state) => state switch
    {
        ServerLifecycleState.NotInstalled => Texts["server.notinstalled"],
        ServerLifecycleState.Stopped => Texts["server.stopped"],
        ServerLifecycleState.StartingDatabase => Texts["server.starting.db"],
        ServerLifecycleState.StartingAuth => Texts["server.starting.auth"],
        ServerLifecycleState.InitializingWorld => Texts["server.init.world"],
        ServerLifecycleState.Ready => Texts["server.ready"],
        ServerLifecycleState.Stopping => Texts["server.stopping"],
        ServerLifecycleState.RestartRequired => Texts["server.restart"],
        ServerLifecycleState.Updating => Texts["server.updating"],
        ServerLifecycleState.RollingBack => Texts["server.rollback"],
        _ => Texts["server.error"]
    };

    private static string FormatBytes(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB"];
        var value = (double)bytes;
        var unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }
        return $"{value:0.#} {units[unit]}";
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _runtime.Changed -= RuntimeChanged;
        _runtime.Server.StateChanged -= RuntimeChanged;
        _runtime.Server.OutputReceived -= ServerOutputReceived;
        _runtime.Loc.Changed -= LanguageChanged;
        Mods.Addons.Dispose();
        Home.Dispose();
        Settings.Dispose();
    }
}
