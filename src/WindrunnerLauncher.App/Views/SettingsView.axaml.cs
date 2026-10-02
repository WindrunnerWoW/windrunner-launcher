using System.Diagnostics;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Platform.Storage;
using Avalonia.Markup.Xaml;
using WindrunnerLauncher.App.ViewModels;
using WindrunnerLauncher.App.Views.Dialogs;
using WindrunnerLauncher.Core.Updates;

namespace WindrunnerLauncher.App.Views;

public sealed partial class SettingsView : UserControl
{
    private SettingsViewModel? _attachedViewModel;

    public SettingsView()
    {
        AvaloniaXamlLoader.Load(this);
        DataContextChanged += (_, _) => Attach();
        AttachedToVisualTree += (_, _) => Attach();
        DetachedFromVisualTree += (_, _) => Detach();
    }

    private void Attach()
    {
        Detach();
        if (DataContext is not SettingsViewModel vm)
            return;
        _attachedViewModel = vm;
        vm.RepairWorldRequested += ConfirmRepairWorld;
        vm.GitHubBrowserOpenRequested += OpenGitHubVerificationUrl;
        vm.LauncherRestartRequested += RestartForUpdate;
    }

    private void Detach()
    {
        if (_attachedViewModel is not { } vm)
            return;
        vm.RepairWorldRequested -= ConfirmRepairWorld;
        vm.GitHubBrowserOpenRequested -= OpenGitHubVerificationUrl;
        vm.LauncherRestartRequested -= RestartForUpdate;
        _attachedViewModel = null;
    }

    /// <summary>
    /// The staged build is already swapped into place by <see cref="LauncherSelfUpdate.Stage"/>;
    /// this just relaunches the same path and lets the outgoing process exit normally.
    /// </summary>
    private static void RestartForUpdate()
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

        (Avalonia.Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.Shutdown();
    }

    private static void OpenGitHubVerificationUrl(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) ||
            uri.Scheme != Uri.UriSchemeHttps ||
            !uri.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase))
            return;

        using var process = Process.Start(new ProcessStartInfo(uri.AbsoluteUri)
        {
            UseShellExecute = true
        });
    }

    private async void ConfirmRepairWorld()
    {
        if (DataContext is not SettingsViewModel vm || TopLevel.GetTopLevel(this) is not Window owner)
            return;
        var dialog = new ConfirmDialog(
            "Rebuild the world database?",
            "The realm is stopped, tw_world is dropped and re-imported from the bundled server SQL, " +
            "then the realm is started again if it was running. Any custom world edits are lost. " +
            "Accounts and characters are kept.",
            "Rebuild world");
        if (await dialog.ShowDialog<bool>(owner))
            await vm.RepairWorldConfirmedAsync();
    }

    private async void ChooseBackgroundClicked(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (DataContext is not SettingsViewModel viewModel ||
            TopLevel.GetTopLevel(this) is not { } topLevel)
            return;

        var files = await topLevel.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Choose home background",
            AllowMultiple = false,
            FileTypeFilter =
            [
                new FilePickerFileType("Images")
                {
                    Patterns = ["*.png", "*.jpg", "*.jpeg", "*.webp", "*.bmp"]
                }
            ]
        });

        var file = files.FirstOrDefault();
        if (file is not null && !string.IsNullOrWhiteSpace(file.Path.LocalPath))
            viewModel.SetHomeBackground(file.Path.LocalPath);
    }

    private async void ChooseClientFolderClicked(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (DataContext is not SettingsViewModel viewModel ||
            TopLevel.GetTopLevel(this) is not { } topLevel)
            return;

        var folders = await topLevel.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "Choose the default game client folder",
            AllowMultiple = false
        });

        var folder = folders.FirstOrDefault();
        if (folder is not null)
            await viewModel.SetClientPathAsync(folder.TryGetLocalPath() ?? folder.Path.LocalPath);
    }

    private void ResetBackgroundClicked(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (DataContext is SettingsViewModel viewModel)
            viewModel.ResetHomeBackground();
    }
}
