using System.Collections.ObjectModel;
using Avalonia;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Avalonia.Styling;
using WindrunnerLauncher.Core.Client;
using WindrunnerLauncher.Core.Downloads;
using WindrunnerLauncher.Core.Models;
using WindrunnerLauncher.Core.Platform;

namespace WindrunnerLauncher.App.ViewModels;

public sealed partial class SettingsViewModel : RuntimeViewModel, IDisposable
{
    private CancellationTokenSource? _gitHubSignInCts;
    private bool _disposed;

    public SettingsViewModel(MainViewModel main) : base(main)
    {
        Languages = new ObservableCollection<string>(main.Runtime.Loc.Available);
        BackgroundOptions = new ObservableCollection<BackgroundOptionViewModel>(
            HomeBackgroundCatalog.Bundled.Select(item =>
                new BackgroundOptionViewModel(
                    item.Id,
                    item.Title,
                    HomeBackgroundCatalog.Load(item.AssetUri),
                    SelectBundledBackground)));
        ApplyTheme(main.Runtime.State.Settings.LightMode);
        SyncBackgroundSelection();
    }

    public ObservableCollection<string> Languages { get; }
    public ObservableCollection<BackgroundOptionViewModel> BackgroundOptions { get; }

    public string SelectedLanguage
    {
        get => Main.Runtime.Loc.Language;
        set
        {
            if (string.IsNullOrWhiteSpace(value) || value == Main.Runtime.Loc.Language)
                return;
            Main.Runtime.Loc.Load(value);
            Main.Runtime.State.Settings.Language = Main.Runtime.Loc.Language;
            Save();
            OnPropertyChanged();
        }
    }

    public bool ShowWineSettings => OperatingSystem.IsLinux();

    /// <summary>Labels in <see cref="LinuxRunnerMode"/> order.</summary>
    public IReadOnlyList<string> RunnerModes { get; } =
    [
        "Automatic (Wine if installed, otherwise Proton)",
        "System Wine",
        "Proton (GE-Proton through umu, downloaded for you)",
        "Custom runner path"
    ];

    public int RunnerModeIndex
    {
        get => (int)Main.Runtime.State.Settings.LinuxRunner;
        set
        {
            if (value < 0 || value == (int)Main.Runtime.State.Settings.LinuxRunner)
                return;
            Main.Runtime.State.Settings.LinuxRunner = (LinuxRunnerMode)value;
            Save();
            OnPropertyChanged();
            OnPropertyChanged(nameof(IsCustomRunner));
            OnPropertyChanged(nameof(RunnerStatusText));
        }
    }

    public bool IsCustomRunner => Main.Runtime.State.Settings.LinuxRunner == LinuxRunnerMode.Custom;

    public string RunnerStatusText
    {
        get
        {
            if (!OperatingSystem.IsLinux())
                return "";
            var settings = Main.Runtime.State.Settings;
            if (LinuxRunner.ResolveInstalled(settings, Main.Runtime.Paths) is { } choice)
                return choice.Kind == WineRunnerKind.Wine
                    ? $"Using Wine: {choice.Runner}"
                    : $"Using Proton through {choice.Runner}. GE-Proton is downloaded on the first launch.";
            return settings.LinuxRunner switch
            {
                LinuxRunnerMode.Wine => "Wine was not found. Install it from your distribution, or choose Proton.",
                LinuxRunnerMode.Custom => "Enter the path to wine, proton, or umu-run.",
                _ => "umu-launcher and GE-Proton are downloaded on the first launch (needs python3)."
            };
        }
    }

    public string WineRunnerPath
    {
        get => Main.Runtime.State.Settings.WineRunnerPath ?? "";
        set
        {
            var trimmed = value?.Trim();
            Main.Runtime.State.Settings.WineRunnerPath = string.IsNullOrWhiteSpace(trimmed) ? null : trimmed;
            Save();
            OnPropertyChanged();
            OnPropertyChanged(nameof(RunnerStatusText));
        }
    }

    public bool ShowBranding
    {
        get => Main.Runtime.State.Settings.ShowBranding;
        set
        {
            Main.Runtime.State.Settings.ShowBranding = value;
            Save();
            OnPropertyChanged();
        }
    }

    public bool LightMode
    {
        get => Main.Runtime.State.Settings.LightMode;
        set
        {
            if (Main.Runtime.State.Settings.LightMode == value)
                return;
            Main.Runtime.State.Settings.LightMode = value;
            ApplyTheme(value);
            Save();
            OnPropertyChanged();
        }
    }

    public string HomeBackgroundPath
    {
        get
        {
            var stored = Main.Runtime.State.Settings.HomeBackgroundPath;
            return HomeBackgroundCatalog.IsBundled(stored)
                ? HomeBackgroundCatalog.DisplayName(stored)
                : stored ?? "";
        }
    }

    public void SetHomeBackground(string path)
    {
        Main.Runtime.State.Settings.HomeBackgroundPath = Path.GetFullPath(path);
        Save();
    }

    public void ResetHomeBackground()
    {
        Main.Runtime.State.Settings.HomeBackgroundPath = null;
        Save();
    }

    private void SelectBundledBackground(BackgroundOptionViewModel option)
    {
        var next = HomeBackgroundCatalog.ToStoredValue(option.Id);
        if (string.Equals(Main.Runtime.State.Settings.HomeBackgroundPath, next, StringComparison.Ordinal))
            return;
        Main.Runtime.State.Settings.HomeBackgroundPath = next;
        Save();
    }

    private void SyncBackgroundSelection()
    {
        var selectedId = HomeBackgroundCatalog.SelectedId(Main.Runtime.State.Settings.HomeBackgroundPath);
        foreach (var option in BackgroundOptions)
            option.IsSelected = option.Id == selectedId;
        OnPropertyChanged(nameof(HomeBackgroundPath));
    }


    public string ClientPathText => Main.Runtime.Client.ResolveClientPath();

    public string ClientPathStatus => Main.Runtime.Client.IsValid(ClientPathText)
        ? Main.Runtime.State.Settings.ClientIsManagedCopy
            ? "Managed client — used by every realm without its own client folder."
            : "Existing install, managed in place — used by every realm without its own client folder."
        : "⚠ No WoW executable found in this folder.";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasClientPathError))]
    private string _clientPathError = "";

    public bool HasClientPathError => ClientPathError.Length > 0;

    public async Task SetClientPathAsync(string path)
    {
        ClientPathError = "";
        if (!Main.Runtime.Client.IsValid(path))
        {
            ClientPathError = $"No WoW.exe found in {path}.";
            return;
        }

        var managed = Path.GetFullPath(Main.Runtime.Paths.Client).TrimEnd(Path.DirectorySeparatorChar);
        var isManagedDir = string.Equals(Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar),
            managed, StringComparison.OrdinalIgnoreCase);
        // The managed-copy branch recognizes client/ itself and adopts it without copying.
        await Main.RunGuardedAsync(() => Main.Runtime.Client.ImportAsync(path, createManagedCopy: isManagedDir));
        Main.Runtime.Notify();
        NotifyClientPath();
    }

    [RelayCommand]
    private void UseManagedClient()
    {
        ClientPathError = "";
        Main.Runtime.Client.UseManagedClient();
        Main.Runtime.Notify();
        NotifyClientPath();
    }

    private void NotifyClientPath()
    {
        OnPropertyChanged(nameof(ClientPathText));
        OnPropertyChanged(nameof(ClientPathStatus));
    }

    public bool CheckUpdatesOnStartup
    {
        get => Main.Runtime.State.Settings.CheckUpdatesOnStartup;
        set
        {
            Main.Runtime.State.Settings.CheckUpdatesOnStartup = value;
            Save();
            OnPropertyChanged();
        }
    }

    public bool IgnoreServerVersion
    {
        get => !string.IsNullOrWhiteSpace(Main.Runtime.State.Settings.IgnoredServerRelease);
        set
        {
            // Skipping means "the newest release", not the one already installed.
            if (value)
                Main.Runtime.Updates.IgnoreServerVersion();
            else
                Main.Runtime.Updates.ClearIgnoredServerVersion();
            OnPropertyChanged();
            OnPropertyChanged(nameof(IgnoredVersionText));
        }
    }

    public string IgnoredVersionText => string.IsNullOrWhiteSpace(Main.Runtime.State.Settings.IgnoredServerRelease)
        ? "No server release is ignored."
        : $"Ignoring {Main.Runtime.State.Settings.IgnoredServerRelease}";

    [RelayCommand]
    private Task CheckUpdatesAsync() =>
        Main.RunGuardedAsync(() => Main.Runtime.Updates.CheckAsync());

    public string CurrentLauncherVersionText => $"Running v{Main.Runtime.Updates.CurrentLauncherVersion}";
    public bool LauncherUpdateAvailable => Main.Runtime.Updates.LauncherUpdateAvailable;
    public string LauncherUpdateVersionText => Main.Runtime.Updates.LatestLauncher is { } latest
        ? $"v{latest.Version} is available."
        : "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanInstallLauncherUpdate))]
    private bool _launcherUpdateInProgress;

    public bool CanInstallLauncherUpdate => !LauncherUpdateInProgress;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasLauncherUpdateStatus))]
    private string _launcherUpdateStatus = "";

    public bool HasLauncherUpdateStatus => LauncherUpdateStatus.Length > 0;

    public event Action? LauncherRestartRequested;

    [RelayCommand]
    private async Task InstallLauncherUpdateAsync()
    {
        if (LauncherUpdateInProgress)
            return;

        LauncherUpdateInProgress = true;
        LauncherUpdateStatus = "Downloading and verifying the update…";
        var ok = await Main.RunGuardedAsync(() => Main.Runtime.Updates.UpdateLauncherAsync());
        LauncherUpdateInProgress = false;

        if (ok)
        {
            LauncherUpdateStatus = "Update staged. Restarting…";
            LauncherRestartRequested?.Invoke();
        }
        else
        {
            LauncherUpdateStatus = "The update did not finish. See the error dialog for details.";
        }
    }

    public event Action? RepairWorldRequested;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasClientRepairStatus))]
    private string _clientRepairStatus = "";

    public bool HasClientRepairStatus => ClientRepairStatus.Length > 0;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasWorldRepairStatus))]
    private string _worldRepairStatus = "";

    public bool HasWorldRepairStatus => WorldRepairStatus.Length > 0;

    public bool CanRepairWorld => Main.Status.ServerInstalled;

    [RelayCommand]
    private Task RepairAsync() => RunClientRepairAsync(deep: false);

    [RelayCommand]
    private Task DeepRepairAsync() => RunClientRepairAsync(deep: true);

    private async Task RunClientRepairAsync(bool deep)
    {
        ClientRepairReport? report = null;
        ClientRepairStatus = deep ? "Deep repair running…" : "Repair running…";
        var ok = await Main.RunGuardedAsync(async () =>
        {
            var realm = Main.Runtime.State.SelectedRealm();
            report = deep
                ? await Main.Runtime.Client.DeepRepairAsync(Main.Runtime.Mods, realm)
                : await Main.Runtime.Client.RepairAsync(Main.Runtime.Mods, realm);
        });
        ClientRepairStatus = ok && report is not null
            ? string.Join("\n", report.Actions.Concat(report.Warnings.Select(w => "⚠ " + w)))
            : "Repair did not finish.";
    }

    [RelayCommand]
    private void RepairWorld() => RepairWorldRequested?.Invoke();

    public async Task RepairWorldConfirmedAsync()
    {
        WorldRepairStatus = "Rebuilding the world database… progress is shown in the MariaDB console on the Server tab.";
        var ok = await Main.RunGuardedAsync(() => Main.Runtime.Server.RepairWorldDatabaseAsync());
        WorldRepairStatus = ok
            ? $"World database rebuilt at {DateTime.Now:t}. Accounts and characters were kept."
            : "World database repair did not finish. Check the MariaDB console on the Server tab.";
    }


    public event Action<string>? GitHubBrowserOpenRequested;

    public bool IsGitHubConfigured => Main.Runtime.GitHub.IsConfigured;
    public bool IsGitHubNotConfigured => !IsGitHubConfigured;
    public bool IsGitHubSignedIn => Main.Runtime.GitHub.IsSignedIn;
    public bool IsGitHubSignedOut => !IsGitHubSignedIn;
    public bool CanStartGitHubSignIn => IsGitHubConfigured && IsGitHubSignedOut && !GitHubSignInInProgress;
    public string GitHubLoginText => Main.Runtime.GitHub.Login is { Length: > 0 } login
        ? $"Signed in as {login}"
        : "Not signed in";

    [ObservableProperty]
    private bool _gitHubSignInInProgress;

    partial void OnGitHubSignInInProgressChanged(bool value) => NotifyGitHubState();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasGitHubDeviceCode))]
    private string _gitHubDeviceCode = "";

    public bool HasGitHubDeviceCode => GitHubDeviceCode.Length > 0;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasGitHubSignInStatus))]
    private string _gitHubSignInStatus = "";

    public bool HasGitHubSignInStatus => GitHubSignInStatus.Length > 0;

    [RelayCommand]
    private async Task SignInWithGitHubAsync()
    {
        if (_disposed || GitHubSignInInProgress)
            return;

        _gitHubSignInCts?.Cancel();
        using var cts = new CancellationTokenSource();
        _gitHubSignInCts = cts;

        GitHubSignInInProgress = true;
        GitHubDeviceCode = "";
        GitHubSignInStatus = "Requesting a code from GitHub…";
        try
        {
            var code = await Main.Runtime.GitHub.BeginSignInAsync(cts.Token);
            GitHubDeviceCode = code.UserCode;
            GitHubSignInStatus = $"Enter this code at {code.VerificationUri} — waiting for approval…";
            GitHubBrowserOpenRequested?.Invoke(code.VerificationUri);

            var result = await Main.Runtime.GitHub.CompleteSignInAsync(code, cts.Token);
            GitHubSignInStatus = result.Status switch
            {
                GitHubDevicePollStatus.Success => $"Signed in as {Main.Runtime.GitHub.Login}.",
                GitHubDevicePollStatus.Denied => "Sign-in was cancelled on GitHub.",
                GitHubDevicePollStatus.Expired => "The code expired before it was entered. Try again.",
                _ => result.Message ?? "GitHub sign-in failed."
            };
        }
        catch (OperationCanceledException)
        {
            GitHubSignInStatus = "Sign-in cancelled.";
        }
        catch (Exception ex)
        {
            GitHubSignInStatus = $"Sign-in failed: {ex.Message}";
        }
        finally
        {
            _gitHubSignInCts = null;
            GitHubDeviceCode = "";
            GitHubSignInInProgress = false;
        }
    }

    [RelayCommand]
    private void CancelGitHubSignIn() => _gitHubSignInCts?.Cancel();

    [RelayCommand]
    private void SignOutOfGitHub()
    {
        Main.Runtime.GitHub.SignOut();
        GitHubSignInStatus = "Signed out.";
        NotifyGitHubState();
    }

    private void NotifyGitHubState()
    {
        OnPropertyChanged(nameof(IsGitHubSignedIn));
        OnPropertyChanged(nameof(IsGitHubSignedOut));
        OnPropertyChanged(nameof(CanStartGitHubSignIn));
        OnPropertyChanged(nameof(GitHubLoginText));
    }

    private void Save()
    {
        Main.Runtime.State.SaveSettings();
        Main.Runtime.Notify();
    }

    private static void ApplyTheme(bool lightMode)
    {
        if (Application.Current is not null)
            Application.Current.RequestedThemeVariant = lightMode ? ThemeVariant.Light : ThemeVariant.Dark;
    }

    internal void Refresh()
    {
        OnPropertyChanged(nameof(ShowBranding));
        OnPropertyChanged(nameof(LightMode));
        SyncBackgroundSelection();
        OnPropertyChanged(nameof(CheckUpdatesOnStartup));
        NotifyClientPath();
        OnPropertyChanged(nameof(IgnoreServerVersion));
        OnPropertyChanged(nameof(IgnoredVersionText));
        OnPropertyChanged(nameof(CanRepairWorld));
        OnPropertyChanged(nameof(LauncherUpdateAvailable));
        OnPropertyChanged(nameof(LauncherUpdateVersionText));
        OnPropertyChanged(nameof(WineRunnerPath));
        OnPropertyChanged(nameof(RunnerModeIndex));
        OnPropertyChanged(nameof(IsCustomRunner));
        OnPropertyChanged(nameof(RunnerStatusText));
        NotifyGitHubState();
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _gitHubSignInCts?.Cancel();
        foreach (var preview in BackgroundOptions.Select(option => option.Preview).OfType<IDisposable>())
            preview.Dispose();
    }
}
