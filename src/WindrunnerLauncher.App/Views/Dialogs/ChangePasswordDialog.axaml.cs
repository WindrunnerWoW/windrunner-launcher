using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;

namespace WindrunnerLauncher.App.Views.Dialogs;

public sealed partial class ChangePasswordDialog : Window
{
    public ChangePasswordDialog() => AvaloniaXamlLoader.Load(this);

    private void CancelClicked(object? sender, RoutedEventArgs e) => Close(null);

    private void SaveClicked(object? sender, RoutedEventArgs e)
    {
        var username = this.FindControl<TextBox>("UsernameBox")!.Text?.Trim() ?? "";
        var password = this.FindControl<TextBox>("PasswordBox")!.Text ?? "";
        if (username.Length < 2 || password.Length < 1)
            return;
        Close(new AccountCredentials(username, password));
    }
}
