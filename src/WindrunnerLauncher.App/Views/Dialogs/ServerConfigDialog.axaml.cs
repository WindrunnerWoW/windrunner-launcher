using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace WindrunnerLauncher.App.Views.Dialogs;

public sealed partial class ServerConfigDialog : Window
{
    public ServerConfigDialog() => AvaloniaXamlLoader.Load(this);

    private void CloseClicked(object? sender, Avalonia.Interactivity.RoutedEventArgs e) => Close(false);
}
