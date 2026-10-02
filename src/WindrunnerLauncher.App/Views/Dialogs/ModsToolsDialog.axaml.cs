using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;

namespace WindrunnerLauncher.App.Views.Dialogs;

public sealed partial class ModsToolsDialog : Window
{
    public ModsToolsDialog() => AvaloniaXamlLoader.Load(this);

    private void CloseClicked(object? sender, RoutedEventArgs e) => Close();
}
