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
        var status = this.FindControl<TextBlock>("StatusText")!;
        if (username.Length < 2 || username.Length > 16 || !username.All(char.IsLetterOrDigit))
        {
            status.Text = "Username must be 2-16 letters or digits.";
            return;
        }
        if (password.Length < 1 || password.Length > 16)
        {
            status.Text = "Password must be 1-16 characters.";
            return;
        }
        Close(new AccountCredentials(username, password));
    }
}

public sealed record AccountCredentials(string Username, string Password);
