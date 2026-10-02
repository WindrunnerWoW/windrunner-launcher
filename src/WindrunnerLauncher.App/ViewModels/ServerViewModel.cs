using System.Collections.ObjectModel;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using WindrunnerLauncher.Core.Models;
using WindrunnerLauncher.Core.Server;
using WindrunnerLauncher.Core.Updates;

namespace WindrunnerLauncher.App.ViewModels;

public sealed partial class ServerViewModel : RuntimeViewModel
{
    private bool _serverActionInProgress;

    public ServerViewModel(MainViewModel main) : base(main)
    {
        Main.PropertyChanged += MainPropertyChanged;
        SetupProgress = new Progress<SetupStage>(stage => Dispatcher.UIThread.Post(() => ApplyStage(stage)));
    }

    public event Action? ForceStopRequested;
    public event Action? CreateAccountRequested;
    public event Action? ChangePasswordRequested;
    public event Action? ChangeGmLevelRequested;

    public ObservableCollection<SetupStage> SetupStages { get; } = [];
    public IProgress<SetupStage> SetupProgress { get; }
    public bool HasSetupProgress => SetupStages.Count > 0;
    public bool IsSetupInProgress { get; private set; }
    public double SetupProgressFraction
    {
        get
        {
            if (SetupStages.Count == 0)
                return 0;
            var completed = SetupStages.Count(s => s.Completed);
            if (SetupStages.Any(s => s.Active || s.Failed))
                return (completed + 0.5d) / SetupStages.Count;
            return (double)completed / SetupStages.Count;
        }
    }
    public string SetupProgressCaption
    {
        get
        {
            var current = SetupStages.FirstOrDefault(s => s.Failed)
                          ?? SetupStages.FirstOrDefault(s => s.Active)
                          ?? SetupStages.LastOrDefault(s => s.Completed);
            if (current is null)
                return IsSetupInProgress ? Texts["onboarding.server.setup"] : "";
            if (current.Failed || current.Active)
                return Texts.Format("setup.step", current.Index, current.Total, current.DisplayName);
            return Texts[_setupCompleteKey];
        }
    }

    private string _setupCompleteKey = "setup.complete";

    /// <summary>Eyebrow over the progress card: first-install setup or a server update/rollback.</summary>
    public string SetupTitle { get; private set; } = "";


    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanCheckServerUpdate))]
    [NotifyPropertyChangedFor(nameof(CheckServerUpdateLabel))]
    private bool _isCheckingServerUpdate;

    public string InstalledVersionText => Main.InstalledServerVersion ?? Texts["server.update.unknown"];
    public string LatestVersionText => Main.LatestServerVersion ?? "—";
    public bool HasServerUpdate => Main.ServerUpdateAvailable;
    private bool LatestIsSkipped => Main.Runtime.Updates.IsLatestServerReleaseSkipped;
    public bool IsServerUpToDate => IsInstalled && Main.LatestServerVersion is not null && !HasServerUpdate && !LatestIsSkipped;
    public bool IsServerUpdateSkipped => LatestIsSkipped;
    public string ServerUpdateVersions => Main.ServerUpdateVersions;
    public bool CanUpdateServer => IsInstalled && !Main.IsBusy && !_serverActionInProgress && (HasServerUpdate || LatestIsSkipped);
    public bool CanCheckServerUpdate => !IsCheckingServerUpdate && !Main.IsBusy;
    public string CheckServerUpdateLabel => IsCheckingServerUpdate ? Texts["server.update.checking"] : Texts["server.update.check"];
    public bool HasServerRollback => Main.Runtime.Updates.HasServerRollback;
    public bool CanRestoreServer => HasServerRollback && !Main.IsBusy && !_serverActionInProgress;
    public string RollbackHint => Main.Runtime.Updates.ServerRollback is { } backup
        ? Texts.Format("server.update.restore.hint", backup.FromVersion, FormatBackupDate(backup.CreatedUtc))
        : "";

    [RelayCommand(CanExecute = nameof(CanUpdateServer))]
    private void UpdateServer()
    {
        if (LatestIsSkipped)
            Main.Runtime.Updates.ClearIgnoredServerVersion();
        Main.ShowServerUpdateCommand.Execute(null);
    }

    [RelayCommand(CanExecute = nameof(CanCheckServerUpdate))]
    private async Task CheckServerUpdateAsync()
    {
        IsCheckingServerUpdate = true;
        try
        {
            var problems = await Main.Runtime.Updates.CheckServerAsync();
            if (problems.Count > 0)
                Main.RequestError(Texts["error.generic"], string.Join(Environment.NewLine, problems));
        }
        catch (Exception ex)
        {
            Main.RequestError(Texts["error.generic"], ex.Message);
        }
        finally
        {
            IsCheckingServerUpdate = false;
            Main.Refresh();
        }
    }

    [RelayCommand(CanExecute = nameof(CanRestoreServer))]
    private void RestoreServer() => Main.RequestServerRollback();


    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasBackupStatus))]
    private string _backupStatus = "";

    public bool HasBackupStatus => BackupStatus.Length > 0;

    public bool CanBackup => IsInstalled && !Main.IsBusy && !_serverActionInProgress
        && Main.Status.ServerState is ServerLifecycleState.Stopped
            or ServerLifecycleState.Ready
            or ServerLifecycleState.RestartRequired
            or ServerLifecycleState.Error;

    public string LastBackupText => Main.Runtime.Backups.Latest is { } latest
        ? Texts.Format("server.backup.last",
            Texts[latest.Kind == ServerBackupKind.Full ? "server.backup.kind.full" : "server.backup.kind.characters"],
            latest.CreatedLocal.ToString("g"))
        : Texts["server.backup.none"];

    [RelayCommand(CanExecute = nameof(CanBackup))]
    private Task BackupFullAsync() => CreateBackupAsync(ServerBackupKind.Full);

    [RelayCommand(CanExecute = nameof(CanBackup))]
    private Task BackupCharactersAsync() => CreateBackupAsync(ServerBackupKind.Characters);

    [RelayCommand]
    private void OpenBackups()
    {
        try
        {
            Directory.CreateDirectory(Main.Runtime.Backups.Root);
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(Main.Runtime.Backups.Root)
            {
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            Main.RequestError(Texts["error.generic"], ex.Message);
        }
    }

    private async Task CreateBackupAsync(ServerBackupKind kind)
    {
        BackupStatus = Texts[kind == ServerBackupKind.Full ? "server.backup.running.full" : "server.backup.running.characters"];
        ServerBackupInfo? created = null;
        await RunServerActionAsync(async () => created = await Main.Runtime.Backups.CreateAsync(kind));
        BackupStatus = created is null ? "" : Texts.Format("server.backup.done", Path.GetFileName(created.Directory));
        OnPropertyChanged(nameof(LastBackupText));
    }

    internal static string FormatBackupDate(string createdUtc) =>
        DateTime.TryParse(createdUtc, null, System.Globalization.DateTimeStyles.RoundtripKind, out var created)
            ? created.ToLocalTime().ToString("g")
            : createdUtc;
    public string StateText => Main.ServerStateText;
    public string StateDetail => Main.Status.ServerState switch
    {
        ServerLifecycleState.Ready => "All local services are accepting connections.",
        ServerLifecycleState.StartingDatabase => "Preparing the portable database…",
        ServerLifecycleState.StartingAuth => "Connecting the authentication service…",
        ServerLifecycleState.InitializingWorld => "Loading maps, scripts, and world state…",
        ServerLifecycleState.Error => "A service stopped unexpectedly. Review its console output.",
        _ => "Portable server lifecycle and configuration."
    };
    public string MariaDbLog => EmptyLog(Main.Runtime.Server.MysqlLog.Text, "Database output appears here while the local realm is running.");
    public string RealmdLog => EmptyLog(Main.Runtime.Server.RealmLog.Text, "realmd console output is retained for this launcher session.");
    public string MangosdLog => EmptyLog(Main.Runtime.Server.MangosLog.Text, "World server console is ready.");
    public bool IsInstalled => Main.Status.ServerInstalled;
    public bool IsRunning => Main.IsServerRunning;
    public bool CanStart => !Main.IsBusy && !_serverActionInProgress
        && IsInstalled && Main.Status.ServerState is ServerLifecycleState.Stopped;
    public bool CanStop => !Main.IsBusy && !_serverActionInProgress
        && Main.Status.ServerState is (ServerLifecycleState.Ready or ServerLifecycleState.RestartRequired);
    public bool CanStartStop => CanStart || CanStop;
    public bool CanCreateAccount => IsInstalled && !Main.IsBusy && !_serverActionInProgress;
    public bool CanManageAccounts => CanCreateAccount;
    public bool CanSendConsoleCommand => !Main.IsBusy && !_serverActionInProgress
        && Main.Status.ServerState is (ServerLifecycleState.Ready or ServerLifecycleState.RestartRequired)
        && !string.IsNullOrWhiteSpace(ConsoleCommand);
    public string StartStopLabel => IsRunning ? Texts["server.stop"] : Texts["server.start"];

    [ObservableProperty]
    private string _consoleCommand = "";

    partial void OnConsoleCommandChanged(string value)
    {
        OnPropertyChanged(nameof(CanSendConsoleCommand));
        SendConsoleCommandCommand.NotifyCanExecuteChanged();
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsMariaDbSelected))]
    [NotifyPropertyChangedFor(nameof(IsRealmdSelected))]
    [NotifyPropertyChangedFor(nameof(IsWorldSelected))]
    private string _selectedConsole = "world";

    public bool IsMariaDbSelected => SelectedConsole == "mariadb";
    public bool IsRealmdSelected => SelectedConsole == "realmd";
    public bool IsWorldSelected => SelectedConsole == "world";

    [RelayCommand]
    private void SelectConsole(string? id)
    {
        SelectedConsole = id switch
        {
            "mariadb" => "mariadb",
            "realmd" => "realmd",
            _ => "world"
        };
    }

    public string RealmName
    {
        get => Settings.RealmName;
        set { Settings.RealmName = value; SaveSettings(); OnPropertyChanged(); }
    }
    public string RealmAddress
    {
        get => Settings.RealmAddress;
        set { Settings.RealmAddress = value; SaveSettings(); OnPropertyChanged(); }
    }
    public int MysqlPort
    {
        get => Settings.MysqlPort;
        set { Settings.MysqlPort = value; SaveSettings(); OnPropertyChanged(); }
    }
    public bool StartWithClient
    {
        get => Settings.StartServerWithClient;
        set { Settings.StartServerWithClient = value; SaveSettings(); OnPropertyChanged(); }
    }

    private ServerFriendlySettings Settings => Main.Runtime.State.Settings.Server;

    [RelayCommand(CanExecute = nameof(CanStartStop))]
    private Task StartStopAsync()
    {
        if (CanStop)
            return RunServerActionAsync(() => Main.Runtime.Server.StopAsync());
        if (CanStart)
            return RunServerActionAsync(() => Main.Runtime.Server.StartAsync());
        return Task.CompletedTask;
    }

    [RelayCommand]
    private void ForceStop() => ForceStopRequested?.Invoke();

    public Task ForceStopConfirmedAsync() =>
        Main.RunGuardedAsync(() => Main.Runtime.Server.ForceStopAsync());

    [RelayCommand(CanExecute = nameof(CanCreateAccount))]
    private void CreateAccount() => CreateAccountRequested?.Invoke();

    public Task CreateAccountAsync(string username, string password) =>
        Main.RunGuardedAsync(() => Main.Runtime.Server.CreateAccountAsync(username, password));

    [RelayCommand(CanExecute = nameof(CanManageAccounts))]
    private void ChangePassword() => ChangePasswordRequested?.Invoke();

    public Task ChangePasswordAsync(string username, string password) =>
        Main.RunGuardedAsync(() => Main.Runtime.Server.ChangePasswordAsync(username, password));

    [RelayCommand(CanExecute = nameof(CanManageAccounts))]
    private void ChangeGmLevel() => ChangeGmLevelRequested?.Invoke();

    public Task ChangeGmLevelAsync(string username, int gmLevel) =>
        Main.RunGuardedAsync(() => Main.Runtime.Server.SetGmLevelAsync(username, gmLevel));

    [RelayCommand(CanExecute = nameof(CanSendConsoleCommand))]
    private Task SendConsoleCommandAsync() => RunServerActionAsync(() =>
    {
        var command = ConsoleCommand.Trim();
        if (command.Length == 0)
            return Task.CompletedTask;
        Main.Runtime.Server.SendCommand(command);
        ConsoleCommand = "";
        return Task.CompletedTask;
    });

    private void SaveSettings()
    {
        Main.Runtime.State.SaveSettings();
        Main.Runtime.Realms.SyncLocalFromServerSettings();
        if (IsRunning)
            Main.Runtime.Server.MarkRestartRequired();
        Main.Runtime.Notify();
    }

    private async Task RunServerActionAsync(Func<Task> action)
    {
        if (_serverActionInProgress)
            return;

        _serverActionInProgress = true;
        NotifyActionCanExecuteChanged();
        try
        {
            await Main.RunGuardedAsync(action);
        }
        finally
        {
            _serverActionInProgress = false;
            NotifyActionCanExecuteChanged();
        }
    }

    private void MainPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(Main.IsBusy))
            NotifyActionCanExecuteChanged();
    }

    private void NotifyActionCanExecuteChanged()
    {
        OnPropertyChanged(nameof(CanStart));
        OnPropertyChanged(nameof(CanStop));
        OnPropertyChanged(nameof(CanStartStop));
        OnPropertyChanged(nameof(CanCreateAccount));
        OnPropertyChanged(nameof(CanManageAccounts));
        OnPropertyChanged(nameof(CanSendConsoleCommand));
        OnPropertyChanged(nameof(CanUpdateServer));
        OnPropertyChanged(nameof(CanCheckServerUpdate));
        OnPropertyChanged(nameof(CanRestoreServer));
        OnPropertyChanged(nameof(CanBackup));
        BackupFullCommand.NotifyCanExecuteChanged();
        BackupCharactersCommand.NotifyCanExecuteChanged();
        UpdateServerCommand.NotifyCanExecuteChanged();
        CheckServerUpdateCommand.NotifyCanExecuteChanged();
        RestoreServerCommand.NotifyCanExecuteChanged();
        StartStopCommand.NotifyCanExecuteChanged();
        CreateAccountCommand.NotifyCanExecuteChanged();
        ChangePasswordCommand.NotifyCanExecuteChanged();
        ChangeGmLevelCommand.NotifyCanExecuteChanged();
        SendConsoleCommandCommand.NotifyCanExecuteChanged();
    }

    internal void BeginSetupProgress() =>
        BeginSetupProgress(SetupOrchestrator.Catalog, "setup.complete", "setup.progress");

    internal void BeginSetupProgress(IReadOnlyList<SetupStageInfo> catalog, string completeKey, string titleKey = "server.update.progress")
    {
        _setupCompleteKey = completeKey;
        SetupTitle = Texts[titleKey];
        OnPropertyChanged(nameof(SetupTitle));
        SetupStages.Clear();
        var total = catalog.Count;
        for (var i = 0; i < total; i++)
        {
            var info = catalog[i];
            SetupStages.Add(new SetupStage
            {
                Id = info.Id,
                DisplayName = LocalizeStage(info.Id, info.DisplayName),
                Index = i + 1,
                Total = total
            });
        }

        IsSetupInProgress = true;
        NotifySetupProgress();
    }

    internal void EndSetupProgress()
    {
        foreach (var stage in SetupStages.Where(s => s.Active).ToList())
        {
            var index = SetupStages.IndexOf(stage);
            if (index >= 0)
            {
                stage.Active = false;
                SetupStages[index] = CopyStage(stage);
            }
        }

        IsSetupInProgress = false;
        NotifySetupProgress();
    }

    internal void Refresh()
    {
        OnPropertyChanged(nameof(StateText));
        OnPropertyChanged(nameof(StateDetail));
        OnPropertyChanged(nameof(IsInstalled));
        OnPropertyChanged(nameof(IsRunning));
        OnPropertyChanged(nameof(InstalledVersionText));
        OnPropertyChanged(nameof(LatestVersionText));
        OnPropertyChanged(nameof(HasServerUpdate));
        OnPropertyChanged(nameof(IsServerUpToDate));
        OnPropertyChanged(nameof(IsServerUpdateSkipped));
        OnPropertyChanged(nameof(ServerUpdateVersions));
        OnPropertyChanged(nameof(HasServerRollback));
        OnPropertyChanged(nameof(RollbackHint));
        OnPropertyChanged(nameof(CheckServerUpdateLabel));
        OnPropertyChanged(nameof(LastBackupText));
        NotifyActionCanExecuteChanged();
        OnPropertyChanged(nameof(StartStopLabel));
        RefreshLogs();
        NotifySetupProgress();
    }

    internal void RefreshLogs()
    {
        OnPropertyChanged(nameof(MariaDbLog));
        OnPropertyChanged(nameof(RealmdLog));
        OnPropertyChanged(nameof(MangosdLog));
    }

    private void ApplyStage(SetupStage incoming)
    {
        incoming.DisplayName = LocalizeStage(incoming.Id, incoming.DisplayName);
        incoming.Active = !incoming.Completed && string.IsNullOrWhiteSpace(incoming.Detail);
        if (incoming.Total <= 0)
            incoming.Total = SetupStages.Count > 0 ? SetupStages.Count : SetupOrchestrator.Catalog.Count;

        var matched = false;
        for (var i = 0; i < SetupStages.Count; i++)
        {
            var existing = SetupStages[i];
            if (existing.Id == incoming.Id)
            {
                if (incoming.Index <= 0)
                    incoming.Index = existing.Index;
                SetupStages[i] = incoming;
                matched = true;
            }
            else if (existing.Active)
            {
                existing.Active = false;
                SetupStages[i] = CopyStage(existing);
            }
        }

        if (!matched)
            SetupStages.Add(incoming);

        NotifySetupProgress();
    }

    private void NotifySetupProgress()
    {
        OnPropertyChanged(nameof(HasSetupProgress));
        OnPropertyChanged(nameof(IsSetupInProgress));
        OnPropertyChanged(nameof(SetupProgressFraction));
        OnPropertyChanged(nameof(SetupProgressCaption));
        Main.RaiseSetupProgress();
    }

    private string LocalizeStage(string id, string fallback)
    {
        var key = $"setup.stage.{id}";
        var text = Texts[key];
        return string.Equals(text, key, StringComparison.OrdinalIgnoreCase) ? fallback : text;
    }

    private static SetupStage CopyStage(SetupStage stage) => new()
    {
        Id = stage.Id,
        DisplayName = stage.DisplayName,
        Completed = stage.Completed,
        Active = stage.Active,
        Detail = stage.Detail,
        Index = stage.Index,
        Total = stage.Total
    };

    /// <summary>Characters shown per console; the full capture is far too large to lay out as one TextBlock.</summary>
    private const int DisplayChars = 32 * 1024;

    private static string EmptyLog(string value, string empty)
    {
        if (string.IsNullOrWhiteSpace(value))
            return empty;
        if (value.Length <= DisplayChars)
            return value;
        var lineStart = value.IndexOf('\n', value.Length - DisplayChars);
        return lineStart < 0 ? value[^DisplayChars..] : value[(lineStart + 1)..];
    }
}
