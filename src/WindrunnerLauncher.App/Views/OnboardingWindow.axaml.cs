using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Platform.Storage;
using WindrunnerLauncher.App.ViewModels;

namespace WindrunnerLauncher.App.Views;

public sealed partial class OnboardingWindow : Window
{
    public OnboardingWindow()
    {
        AvaloniaXamlLoader.Load(this);
        DataContextChanged += (_, _) => Attach();
        Closed += (_, _) => (DataContext as OnboardingViewModel)?.Detach();
    }

    private void Attach()
    {
        if (DataContext is not OnboardingViewModel vm)
            return;
        vm.LocateRequested -= LocateClient;
        vm.LocateRequested += LocateClient;
        vm.Completed -= Complete;
        vm.Completed += Complete;
    }

    private async void LocateClient()
    {
        if (DataContext is not OnboardingViewModel vm)
            return;
        var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "Locate the game client",
            AllowMultiple = false
        });
        var folder = folders.FirstOrDefault();
        if (folder is not null)
            vm.ExistingClientPath = folder.TryGetLocalPath() ?? folder.Path.LocalPath;
    }

    private void Complete() => Close(true);

    private void HeaderPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
            BeginMoveDrag(e);
    }

    private void CloseClicked(object? sender, RoutedEventArgs e) => Close(false);
}
