using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;

namespace WindrunnerLauncher.App.Views.Dialogs;

public sealed partial class CreateAccountDialog : Window
{
    public CreateAccountDialog() => AvaloniaXamlLoader.Load(this);

    private void CancelClicked(object? sender, RoutedEventArgs e) => Close(null);

    private void CreateClicked(object? sender, RoutedEventArgs e)
    {
        var username = this.FindControl<TextBox>("UsernameBox")!.Text?.Trim() ?? "";
        var password = this.FindControl<TextBox>("PasswordBox")!.Text ?? "";
        if (username.Length < 3 || password.Length < 3)
            return;
        Close(new AccountCredentials(username, password));
    }
}

public sealed record AccountCredentials(string Username, string Password);
