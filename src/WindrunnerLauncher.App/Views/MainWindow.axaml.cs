using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Controls.Shapes;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Platform;
using WindrunnerLauncher.App.ViewModels;
using WindrunnerLauncher.App.Views.Dialogs;
using WindrunnerLauncher.Core.Client;
using WindrunnerLauncher.Core.Models;
using WindrunnerLauncher.Core.Updates;

namespace WindrunnerLauncher.App.Views;

public sealed partial class MainWindow : Window
{
    private readonly TrayIcon _trayIcon;
    private MainViewModel? _attachedViewModel;
    private bool _exitRequested;
    private bool _stopAndExitInProgress;
    private bool _onboardingOpen;
    private bool _launcherUpdatePromptPending;
    private bool _hiddenForClient;

    public MainWindow()
    {
        AvaloniaXamlLoader.Load(this);
        Closing += WindowClosing;
        Opened += WindowOpened;
        DataContextChanged += (_, _) => AttachViewModel();
        Closed += (_, _) => DetachViewModel();

        var open = new NativeMenuItem(ViewModel?.Texts["tray.open"] ?? "Open Launcher");
        open.Click += OpenFromTray;
        var stopAndExit = new NativeMenuItem(ViewModel?.Texts["tray.stop.exit"] ?? "Stop Server & Exit");
        stopAndExit.Click += StopAndExit;
        WindowIcon? trayIcon = null;
        try
        {
            using var stream = AssetLoader.Open(new Uri("avares://WindrunnerLauncher/Assets/icon.png"));
            trayIcon = new WindowIcon(stream);
        }
        catch
        {
            // Tray still works without a custom glyph.
        }

        _trayIcon = new TrayIcon
        {
            ToolTipText = ViewModel?.Title ?? "Realm Launcher",
            IsVisible = true,
            Icon = trayIcon,
            Menu = new NativeMenu
            {
                Items =
                {
                    open,
                    new NativeMenuItemSeparator(),
                    stopAndExit
                }
            }
        };
        _trayIcon.Clicked += OpenFromTray;
    }

    private MainViewModel? ViewModel => DataContext as MainViewModel;

    private void AttachViewModel()
    {
        DetachViewModel();
        if (ViewModel is not { } vm)
            return;
        _attachedViewModel = vm;
        vm.OnboardingRequested += ShowOnboarding;
        vm.CreateAccountPromptRequested += ShowCreateAccount;
        vm.ClientLaunched += HideForClient;
        vm.ClientExited += RestoreAfterClientExit;
        vm.ErrorRequested += ShowError;
        vm.RealmEditorRequested += ShowRealmEditor;
        vm.ServerUpdateRequested += ShowServerUpdate;
        vm.ServerRollbackRequested += ShowServerRollback;
        vm.LauncherUpdateRequested += ShowLauncherUpdate;
        vm.LauncherRestartRequested += RestartForLauncherUpdate;
    }

    private void DetachViewModel()
    {
        if (_attachedViewModel is not { } vm)
            return;
        vm.OnboardingRequested -= ShowOnboarding;
        vm.CreateAccountPromptRequested -= ShowCreateAccount;
        vm.ClientLaunched -= HideForClient;
        vm.ClientExited -= RestoreAfterClientExit;
        vm.ErrorRequested -= ShowError;
        vm.RealmEditorRequested -= ShowRealmEditor;
        vm.ServerUpdateRequested -= ShowServerUpdate;
        vm.ServerRollbackRequested -= ShowServerRollback;
        vm.LauncherUpdateRequested -= ShowLauncherUpdate;
        vm.LauncherRestartRequested -= RestartForLauncherUpdate;
        _attachedViewModel = null;
    }

    private async void ShowServerUpdate()
    {
        if (ViewModel is not { } vm)
            return;

        var dialog = new ServerUpdateDialog(vm);
        var choice = await dialog.ShowDialog<ServerUpdateChoice>(this);
        switch (choice)
        {
            case ServerUpdateChoice.Update:
                await vm.UpdateServerConfirmedAsync();
                break;
            case ServerUpdateChoice.Skip:
                vm.SkipServerUpdateCommand.Execute(null);
                break;
        }
    }

    private async void ShowServerRollback()
    {
        if (ViewModel is not { } vm || vm.Runtime.Updates.ServerRollback is not { } backup)
            return;

        var texts = vm.Texts;
        var confirmed = await new ConfirmDialog(
            texts.Format("server.update.restore.title", backup.FromVersion),
            texts.Format("server.update.restore.message", backup.FromVersion, ServerViewModel.FormatBackupDate(backup.CreatedUtc)),
            texts["server.update.restore.confirm"]).ShowDialog<bool>(this);
        if (confirmed)
            await vm.RollbackServerConfirmedAsync();
    }

    private async void ShowLauncherUpdate()
    {
        if (ViewModel is not { } vm)
            return;

        // Onboarding owns the screen on a fresh install; show the update prompt once it closes.
        if (_onboardingOpen)
        {
            _launcherUpdatePromptPending = true;
            return;
        }

        if (!vm.LauncherUpdateAvailable)
            return;

        var choice = await new LauncherUpdateDialog(vm).ShowDialog<LauncherUpdateChoice>(this);
        switch (choice)
        {
            case LauncherUpdateChoice.Update:
                await vm.InstallLauncherUpdateAsync();
                break;
            case LauncherUpdateChoice.Ignore:
                vm.IgnoreLauncherUpdateCommand.Execute(null);
                break;
        }
    }

    /// <summary>
    /// The staged build is already swapped into place by <see cref="LauncherSelfUpdate.Stage"/>;
    /// this just relaunches the same path and lets the outgoing process exit normally.
    /// </summary>
    private static void RestartForLauncherUpdate()
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo(LauncherSelfUpdate.CurrentExecutablePath())
            {
                UseShellExecute = true
            });
        }
        catch
        {
            // Best effort — the user can relaunch manually if this fails.
            return;
        }

        (Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.Shutdown();
    }

    private void WindowOpened(object? sender, EventArgs e)
    {
        var workArea = Screens.Primary?.WorkingArea;
        var availableWidth = workArea?.Width ?? 1280;
        var availableHeight = workArea?.Height ?? 800;
        var width = Math.Min(1280, availableWidth);
        var height = Math.Min(800, availableHeight);
        Position = new PixelPoint(
            (workArea?.X ?? 0) + Math.Max(0, availableWidth - width) / 2,
            (workArea?.Y ?? 0) + Math.Max(0, availableHeight - height) / 2);
        Width = width;
        Height = height;
        WindowState = WindowState.Normal;
        AttachViewModel();
        ViewModel?.Runtime.Updates.SelfUpdate.MarkHealthy();
        UpdateMaximizeGlyph();
        if (ViewModel is { Runtime.State.Settings.OnboardingCompleted: false })
            ShowOnboarding();
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == WindowStateProperty)
            UpdateMaximizeGlyph();
    }

    private void WindowClosing(object? sender, WindowClosingEventArgs e)
    {
        if (_exitRequested)
            return;

        if (ViewModel?.IsServerRunning == true)
        {
            // Closing the launcher is the one thing that stops the local realm.
            e.Cancel = true;
            StopAndExit(sender, e);
            return;
        }

        _exitRequested = true;
        _trayIcon.IsVisible = false;
        (Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.Shutdown();
    }

    private void OpenFromTray(object? sender, EventArgs e)
    {
        _hiddenForClient = false;
        Show();
        WindowState = WindowState.Normal;
        Activate();
    }

    private void HideForClient()
    {
        _hiddenForClient = true;
        Hide();
    }

    /// <summary>
    /// Brings the launcher back when the game exits, but only if it was the launch that hid it;
    /// a user who reopened it from the tray during the session is left alone.
    /// </summary>
    private void RestoreAfterClientExit()
    {
        if (!_hiddenForClient || _exitRequested)
            return;

        _hiddenForClient = false;
        Show();
        WindowState = WindowState.Normal;
        Activate();
    }

    private async void StopAndExit(object? sender, EventArgs e)
    {
        if (_stopAndExitInProgress)
            return;
        _stopAndExitInProgress = true;

        if (ViewModel is { } vm && vm.IsServerRunning)
        {
            Hide();
            // Graceful saveall + shutdown, force-killed after 60s so exit never hangs.
            try { await vm.Runtime.Server.StopAsync(force: true); }
            catch { /* runtime disposal on exit kills whatever is left */ }
        }

        _exitRequested = true;
        _trayIcon.IsVisible = false;
        Close();
        (Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.Shutdown();
    }

    private void HeaderPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
            BeginMoveDrag(e);
    }

    private void SettingsChromeClicked(object? sender, RoutedEventArgs e) =>
        ViewModel?.NavigateCommand.Execute("settings");

    private void MinimizeClicked(object? sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void MaximizeClicked(object? sender, RoutedEventArgs e) =>
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    private void CloseClicked(object? sender, RoutedEventArgs e) => Close();

    private void UpdateMaximizeGlyph()
    {
        var maximized = WindowState == WindowState.Maximized;
        if (this.FindControl<Rectangle>("MaximizeIcon") is { } maximizeIcon)
            maximizeIcon.IsVisible = !maximized;
        if (this.FindControl<Avalonia.Controls.Shapes.Path>("RestoreIcon") is { } restoreIcon)
            restoreIcon.IsVisible = maximized;
        if (this.FindControl<Button>("MaximizeButton") is { } maximizeButton)
            ToolTip.SetTip(maximizeButton, maximized ? "Restore" : "Maximize");
    }

    private async void ShowOnboarding()
    {
        if (ViewModel is null)
            return;
        var window = new OnboardingWindow
        {
            DataContext = new OnboardingViewModel(ViewModel)
        };
        _onboardingOpen = true;
        try
        {
            await window.ShowDialog(this);
        }
        finally
        {
            _onboardingOpen = false;
        }

        if (_launcherUpdatePromptPending)
        {
            _launcherUpdatePromptPending = false;
            ShowLauncherUpdate();
        }
    }

    private async void ShowCreateAccount()
    {
        if (ViewModel is not { } vm)
            return;
        var result = await new CreateAccountDialog().ShowDialog<AccountCredentials?>(this);
        if (result is not null)
            await vm.Server.CreateAccountAsync(result.Username, result.Password);
    }

    private async void ShowError(string title, string detail)
    {
        await new ErrorDialog(title, detail).ShowDialog(this);
    }

    private async void ShowRealmEditor(RealmEntry? existing)
    {
        if (ViewModel is not { } vm)
            return;

        // The local realm falls back to what the bundled server listens on; remote realms use the
        // vanilla default unless an override is enabled.
        var defaultPort = existing?.Id == RealmEntry.LocalServerId
            ? vm.Runtime.State.Settings.Server.AuthPort
            : RealmlistWriter.DefaultAuthPort;
        var edit = new RealmEditViewModel(existing, defaultPort);
        var dialog = new RealmEditDialog { DataContext = edit };
        edit.Saved += realm =>
        {
            if (existing is null)
            {
                ViewModel.Runtime.Realms.Add(realm);
                ViewModel.Realms.Add(realm);
            }
            else
            {
                ViewModel.Runtime.Realms.Update(realm);
            }
            ViewModel.SelectedRealm = realm;
            ViewModel.Refresh();
            dialog.Close(true);
        };
        edit.Cancelled += () => dialog.Close(false);
        await dialog.ShowDialog<bool>(this);
    }

    protected override void OnClosed(EventArgs e)
    {
        _trayIcon.Dispose();
        base.OnClosed(e);
    }
}
