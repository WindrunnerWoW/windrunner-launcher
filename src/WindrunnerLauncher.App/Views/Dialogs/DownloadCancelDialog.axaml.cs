using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;

namespace WindrunnerLauncher.App.Views.Dialogs;

public sealed partial class DownloadCancelDialog : Window
{
    public DownloadCancelDialog() => AvaloniaXamlLoader.Load(this);
    private void KeepClicked(object? sender, RoutedEventArgs e) => Close(false);
    private void CancelClicked(object? sender, RoutedEventArgs e) => Close(true);
}
