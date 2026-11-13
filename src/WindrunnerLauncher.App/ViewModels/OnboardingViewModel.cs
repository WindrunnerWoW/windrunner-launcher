    using System.Collections.ObjectModel;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using WindrunnerLauncher.Core.Client;
using WindrunnerLauncher.Core.Downloads;
using WindrunnerLauncher.Core.Models;

namespace WindrunnerLauncher.App.ViewModels;

public sealed partial class OnboardingViewModel : ObservableObject
{
    private readonly MainViewModel _main;
    private CancellationTokenSource? _gitHubSignInCts;
    private bool _detached;

    public OnboardingViewModel(MainViewModel main)
    {
        _main = main;
        Texts = main.Texts;
        Languages = new ObservableCollection<string>(main.Runtime.Loc.Available);
        _selectedLanguage = main.Runtime.Loc.Language;
        _installChoice = InstallChoice.ClientAndLocalServer;
        _createManagedCopy = false;
        _downloadCleanClient = CanDownloadClient;
        // A client from an earlier, unfinished setup is reused instead of downloaded again.
        _useInstalledClient = InstalledClientPath is not null;
        _main.PropertyChanged += OnMainPropertyChanged;
        _main.Runtime.Mods.Changed += OnModsChanged;
        _main.Runtime.GitHub.Changed += OnGitHubChanged;
    }

    public event Action? LocateRequested;
    public event Action? Completed;
    public event Action<string>? GitHubBrowserOpenRequested;

    public LocalizedStrings Texts { get; }
    public ObservableCollection<string> Languages { get; }
    public bool IsEnglishFirstLaunch => _main.Runtime.State.Settings.OnboardingCompleted is false;

    [ObservableProperty]
    private string _selectedLanguage;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsClientOnly))]
    [NotifyPropertyChangedFor(nameof(IsClientAndServer))]
    private InstallChoice _installChoice;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsDownloadSource))]
    [NotifyPropertyChangedFor(nameof(IsLocateSource))]
    [NotifyPropertyChangedFor(nameof(CanContinue))]
    private bool _downloadCleanClient;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsInstalledSource))]
    [NotifyPropertyChangedFor(nameof(IsDownloadSource))]
    [NotifyPropertyChangedFor(nameof(IsLocateSource))]
    [NotifyPropertyChangedFor(nameof(CanContinue))]
    private bool _useInstalledClient;

    [ObservableProperty]
    private bool _createManagedCopy;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanContinue))]
    private string? _existingClientPath;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanContinue))]
    [NotifyPropertyChangedFor(nameof(ShowHelpText))]
    private bool _isWorking;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowHelpText))]
    [NotifyPropertyChangedFor(nameof(ShowStatusText))]
    private string _progressText = "";

    [ObservableProperty]
    private double _progressValue;

    [ObservableProperty]
    private bool _progressIndeterminate;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowHelpText))]
    [NotifyPropertyChangedFor(nameof(ShowStatusText))]
    private bool _hasProgress;

    private bool _serverSetupActive;

    public bool ShowHelpText => !HasProgress && string.IsNullOrWhiteSpace(ProgressText);
    public bool ShowStatusText => !HasProgress && !string.IsNullOrWhiteSpace(ProgressText);

    public bool IsClientOnly
    {
        get => InstallChoice == InstallChoice.ClientOnly;
        set { if (value) InstallChoice = InstallChoice.ClientOnly; }
    }
    public bool IsClientAndServer
    {
        get => InstallChoice == InstallChoice.ClientAndLocalServer;
        set { if (value) InstallChoice = InstallChoice.ClientAndLocalServer; }
    }
    /// <summary>
    /// A usable client that is already on disk: the one a previous setup run configured, or else a
    /// client copied by hand into the launcher's own <c>client/</c> folder. Null when neither exists.
    /// </summary>
    public string? InstalledClientPath
    {
        get
        {
            var path = _main.Runtime.Client.ResolveClientPath();
            return _main.Runtime.Client.IsValid(path) ? path : null;
        }
    }
    public bool HasInstalledClient => InstalledClientPath is not null;
    public string InstalledClientLabel => $"Use the installed client ({InstalledClientPath})";
    public bool IsInstalledSource
    {
        get => UseInstalledClient;
        set { if (value) UseInstalledClient = true; }
    }
    public bool IsDownloadSource
    {
        get => !UseInstalledClient && DownloadCleanClient;
        set { if (value) { UseInstalledClient = false; DownloadCleanClient = true; } }
    }
    public bool IsLocateSource
    {
        get => !UseInstalledClient && !DownloadCleanClient;
        set { if (value) { UseInstalledClient = false; DownloadCleanClient = false; } }
    }
    public bool CanDownloadClient => ClientManager.CanBootstrap(_main.Runtime.Mods.Manifest);
    public bool IsDownloadUnavailable => !CanDownloadClient;
    public bool CanContinue => !IsWorking && (UseInstalledClient
        ? HasInstalledClient
        : DownloadCleanClient ? CanDownloadClient : !string.IsNullOrWhiteSpace(ExistingClientPath));
    public bool CanSkip => !IsWorking;

    partial void OnSelectedLanguageChanged(string value)
    {
        _main.Runtime.Loc.Load(value);
        Texts.Refresh();
        NotifyGitHubState();
    }

    public bool IsGitHubConfigured => _main.Runtime.GitHub.IsConfigured;
    public bool IsGitHubNotConfigured => !IsGitHubConfigured;
    public bool IsGitHubSignedIn => _main.Runtime.GitHub.IsSignedIn;
    public bool IsGitHubSignedOut => !IsGitHubSignedIn;
    public bool CanStartGitHubSignIn => IsGitHubConfigured && IsGitHubSignedOut && !GitHubSignInInProgress;
    public string GitHubLoginText => _main.Runtime.GitHub.Login is { Length: > 0 } login
        ? Texts.Format("onboarding.github.signedin.as", login)
        : Texts["onboarding.github.notsignedin"];

    /// <summary>GitHub dropped the stored sign-in, so the user has to approve a new one.</summary>
    public bool NeedsGitHubReauth => _main.Runtime.GitHub.NeedsReauth;

    public string GitHubReauthText => _main.Runtime.GitHub.LastSignOutReason switch
    {
        GitHubSignOutReason.TokenRejected => Texts["onboarding.github.reauth.rejected"],
        GitHubSignOutReason.TokenExpired => Texts["onboarding.github.reauth.expired"],
        _ => ""
    };

    public string GitHubSignInButtonText => NeedsGitHubReauth
        ? Texts["onboarding.github.signin.again"]
        : Texts["onboarding.github.signin"];

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
        if (_detached || GitHubSignInInProgress)
            return;

        _gitHubSignInCts?.Cancel();
        using var cts = new CancellationTokenSource();
        _gitHubSignInCts = cts;

        GitHubSignInInProgress = true;
        GitHubDeviceCode = "";
        GitHubSignInStatus = Texts["onboarding.github.requesting"];
        try
        {
            var code = await _main.Runtime.GitHub.BeginSignInAsync(cts.Token);
            GitHubDeviceCode = code.UserCode;
            GitHubSignInStatus = Texts.Format("onboarding.github.enter", code.VerificationUri);
            GitHubBrowserOpenRequested?.Invoke(code.VerificationUri);

            var result = await _main.Runtime.GitHub.CompleteSignInAsync(code, cts.Token);
            GitHubSignInStatus = result.Status switch
            {
                GitHubDevicePollStatus.Success => GitHubLoginText,
                GitHubDevicePollStatus.Denied => Texts["onboarding.github.denied"],
                GitHubDevicePollStatus.Expired => Texts["onboarding.github.expired"],
                _ => result.Message ?? Texts["onboarding.github.failed"]
            };
        }
        catch (OperationCanceledException)
        {
            GitHubSignInStatus = Texts["onboarding.github.cancelled"];
        }
        catch (Exception ex)
        {
            GitHubSignInStatus = Texts.Format("onboarding.github.failed.detail", ex.Message);
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
        _main.Runtime.GitHub.SignOut();
        GitHubSignInStatus = Texts["onboarding.github.signedout"];
        NotifyGitHubState();
    }

    private void OnGitHubChanged() => Dispatcher.UIThread.Post(NotifyGitHubState);

    private void NotifyGitHubState()
    {
        OnPropertyChanged(nameof(IsGitHubSignedIn));
        OnPropertyChanged(nameof(IsGitHubSignedOut));
        OnPropertyChanged(nameof(CanStartGitHubSignIn));
        OnPropertyChanged(nameof(GitHubLoginText));
        OnPropertyChanged(nameof(NeedsGitHubReauth));
        OnPropertyChanged(nameof(GitHubReauthText));
        OnPropertyChanged(nameof(GitHubSignInButtonText));
    }

    partial void OnExistingClientPathChanged(string? value) => ContinueCommand.NotifyCanExecuteChanged();
    partial void OnDownloadCleanClientChanged(bool value) => ContinueCommand.NotifyCanExecuteChanged();
    partial void OnUseInstalledClientChanged(bool value) => ContinueCommand.NotifyCanExecuteChanged();

    partial void OnIsWorkingChanged(bool value) => SkipCommand.NotifyCanExecuteChanged();

    private void OnMainPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (!IsWorking)
            return;

        if (_serverSetupActive)
        {
            if (e.PropertyName is nameof(MainViewModel.ProgressBarCaption)
                or nameof(MainViewModel.ProgressBarValue)
                or nameof(MainViewModel.HasProgressBar)
                or nameof(MainViewModel.HasDownload)
                or nameof(MainViewModel.DownloadProgress)
                or nameof(MainViewModel.DownloadText))
                SyncSetupProgress();
            return;
        }

        if (e.PropertyName is nameof(MainViewModel.HasDownload)
            or nameof(MainViewModel.DownloadProgress)
            or nameof(MainViewModel.DownloadIndeterminate)
            or nameof(MainViewModel.DownloadText))
            SyncDownloadProgress();
    }

    private void SyncDownloadProgress()
    {
        if (!_main.HasDownload)
            return;

        HasProgress = true;
        ProgressValue = _main.DownloadProgress;
        ProgressIndeterminate = _main.DownloadIndeterminate;
        if (!string.IsNullOrWhiteSpace(_main.DownloadText))
            ProgressText = _main.DownloadText;
    }

    private void OnModsChanged() => Dispatcher.UIThread.Post(() =>
    {
        OnPropertyChanged(nameof(CanDownloadClient));
        OnPropertyChanged(nameof(IsDownloadUnavailable));
        OnPropertyChanged(nameof(CanContinue));
        ContinueCommand.NotifyCanExecuteChanged();
    });

    private void SyncSetupProgress()
    {
        // Server stages that download (server, SQL, MariaDB, maps) show bytes, not just the stage.
        if (_main.HasDownload)
        {
            HasProgress = true;
            ProgressValue = _main.DownloadProgress;
            ProgressIndeterminate = _main.DownloadIndeterminate;
            var stage = _main.Server.SetupProgressCaption;
            ProgressText = string.IsNullOrWhiteSpace(stage) ? _main.DownloadText : $"{stage} · {_main.DownloadText}";
            return;
        }

        HasProgress = true;
        ProgressIndeterminate = false;
        ProgressValue = _main.Server.SetupProgressFraction;
        if (!string.IsNullOrWhiteSpace(_main.Server.SetupProgressCaption))
            ProgressText = _main.Server.SetupProgressCaption;
    }

    [RelayCommand(CanExecute = nameof(CanSkip))]
    private void Skip()
    {
        var settings = _main.Runtime.State.Settings;
        settings.Language = _main.Runtime.Loc.Language;
        settings.InstallChoice = InstallChoice;
        settings.ClientIsManagedCopy = CreateManagedCopy;
        // Skipping is allowed without a client; the managed client can be downloaded later in Settings.
        settings.OnboardingCompleted = true;
        _main.Runtime.State.SaveSettings();
        _main.CompleteOnboarding();
        Completed?.Invoke();
    }

    /// <summary>Drops subscriptions to launcher-lifetime services. Call when the window closes.</summary>
    public void Detach()
    {
        _detached = true;
        _gitHubSignInCts?.Cancel();
        _main.PropertyChanged -= OnMainPropertyChanged;
        _main.Runtime.Mods.Changed -= OnModsChanged;
        _main.Runtime.GitHub.Changed -= OnGitHubChanged;
    }

    [RelayCommand]
    private void Locate() => LocateRequested?.Invoke();

    [RelayCommand(CanExecute = nameof(CanContinue))]
    private async Task ContinueAsync()
    {
        var settings = _main.Runtime.State.Settings;
        settings.Language = _main.Runtime.Loc.Language;
        settings.InstallChoice = InstallChoice;
        settings.ClientIsManagedCopy = CreateManagedCopy;
        _main.Runtime.State.SaveSettings();

        IsWorking = true;
        HasProgress = true;
        ProgressIndeterminate = true;
        ProgressValue = 0;
        ProgressText = Texts["onboarding.preparing"];
        ContinueCommand.NotifyCanExecuteChanged();
        var success = await _main.RunGuardedAsync(async () =>
        {
            if (UseInstalledClient)
            {
                var installed = InstalledClientPath
                    ?? throw new InvalidOperationException("The installed client is no longer valid. Choose another source.");
                // Adopting records the path and the clean WoW.exe backup, which a hand-copied client
                // never got. Inside the launcher's client/ folder it counts as the managed copy.
                var isLauncherFolder = string.Equals(
                    Path.GetFullPath(installed).TrimEnd(Path.DirectorySeparatorChar),
                    Path.GetFullPath(_main.Runtime.Paths.Client).TrimEnd(Path.DirectorySeparatorChar),
                    StringComparison.OrdinalIgnoreCase);
                ProgressText = Texts["onboarding.importing"];
                await _main.Runtime.Client.ImportAsync(installed, createManagedCopy: isLauncherFolder);
            }
            else if (DownloadCleanClient)
            {
                if (!CanDownloadClient)
                    throw new InvalidOperationException(Texts["onboarding.bootstrap.unavailable"]);

                ProgressText = Texts["onboarding.downloading"];
                await _main.Runtime.Client.BootstrapAsync(
                    _main.Runtime.Mods.Manifest, _main.Runtime.Downloads,
                    status: text => Dispatcher.UIThread.Post(() =>
                    {
                        ProgressIndeterminate = true;
                        ProgressText = text;
                    }));
            }
            else
            {
                ProgressText = Texts["onboarding.importing"];
                ProgressIndeterminate = true;
                await _main.Runtime.Client.ImportAsync(ExistingClientPath!, CreateManagedCopy);
            }

            var clientPath = _main.Runtime.Client.ResolveClientPath();
            if (!_main.Runtime.Client.IsValid(clientPath))
                throw new InvalidOperationException(
                    "Client setup finished without producing a playable WoW client.");

            if (InstallChoice == InstallChoice.ClientAndLocalServer)
            {
                _serverSetupActive = true;
                ProgressText = Texts["onboarding.server.setup"];
                ProgressIndeterminate = false;
                _main.Server.BeginSetupProgress();
                SyncSetupProgress();
                try
                {
                    await _main.Runtime.Server.SetupAsync(_main.Server.SetupProgress);
                }
                finally
                {
                    _main.Server.EndSetupProgress();
                    SyncSetupProgress();
                    _serverSetupActive = false;
                }

                if (!_main.Runtime.Server.IsInstalled)
                    throw new InvalidOperationException(
                        "Local server setup finished without producing an installed server.");
            }
        });
        IsWorking = false;
        HasProgress = false;
        ProgressIndeterminate = false;
        ContinueCommand.NotifyCanExecuteChanged();
        if (!success)
        {
            ProgressText = Texts["onboarding.setup.failed"];
            return;
        }

        settings.OnboardingCompleted = true;
        _main.Runtime.State.SaveSettings();
        _main.CompleteOnboarding();
        Completed?.Invoke();
    }
}
