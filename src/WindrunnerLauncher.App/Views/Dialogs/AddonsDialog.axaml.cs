using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using WindrunnerLauncher.App.ViewModels;

namespace WindrunnerLauncher.App.Views.Dialogs;

public sealed partial class AddonsDialog : Window
{
    public AddonsDialog() => AvaloniaXamlLoader.Load(this);

    private void CloseClicked(object? sender, RoutedEventArgs e) => Close();

    private async void AddAddonClicked(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not AddonsViewModel vm)
            return;
        await new AddAddonDialog { DataContext = vm }.ShowDialog(this);
    }
}
