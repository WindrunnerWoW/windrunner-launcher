using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using WindrunnerLauncher.App.ViewModels;

namespace WindrunnerLauncher.App.Views.Dialogs;

public sealed partial class AddAddonDialog : Window
{
    public AddAddonDialog() => AvaloniaXamlLoader.Load(this);

    private void CancelClicked(object? sender, RoutedEventArgs e) => Close();

    private async void AddClicked(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not AddonsViewModel vm)
            return;
        await vm.AddAddonCommand.ExecuteAsync(null);
        if (!vm.HasAddAddonError)
            Close();
    }
}
