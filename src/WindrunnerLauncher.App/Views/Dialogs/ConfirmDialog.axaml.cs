using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;

namespace WindrunnerLauncher.App.Views.Dialogs;

public sealed partial class ConfirmDialog : Window
{
    public ConfirmDialog() => AvaloniaXamlLoader.Load(this);

    public ConfirmDialog(string heading, string message, string confirmLabel) : this()
    {
        Title = heading;
        this.FindControl<TextBlock>("HeadingText")!.Text = heading;
        this.FindControl<TextBlock>("MessageText")!.Text = message;
        this.FindControl<Button>("ConfirmButton")!.Content = confirmLabel;
    }

    private void CancelClicked(object? sender, RoutedEventArgs e) => Close(false);
    private void ConfirmClicked(object? sender, RoutedEventArgs e) => Close(true);
}
