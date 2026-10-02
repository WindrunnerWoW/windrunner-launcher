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
using WindrunnerLauncher.Core.Models;

namespace WindrunnerLauncher.App.Views;

public sealed partial class MainWindow : Window
{
    private readonly TrayIcon _trayIcon;
    private MainViewModel? _attachedViewModel;
    private bool _exitRequested;
    private bool _stopAndExitInProgress;

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
        vm.ErrorRequested += ShowError;
        vm.RealmEditorRequested += ShowRealmEditor;
        vm.ServerUpdateRequested += ShowServerUpdate;
        vm.ServerRollbackRequested += ShowServerRollback;
    }

    private void DetachViewModel()
    {
        if (_attachedViewModel is not { } vm)
            return;
        vm.OnboardingRequested -= ShowOnboarding;
        vm.ErrorRequested -= ShowError;
        vm.RealmEditorRequested -= ShowRealmEditor;
        vm.ServerUpdateRequested -= ShowServerUpdate;
        vm.ServerRollbackRequested -= ShowServerRollback;
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
        await window.ShowDialog(this);
    }

    private async void ShowError(string title, string detail)
    {
        await new ErrorDialog(title, detail).ShowDialog(this);
    }

    private async void ShowRealmEditor(RealmEntry? existing)
    {
        if (ViewModel is null)
            return;

        var edit = new RealmEditViewModel(existing);
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
