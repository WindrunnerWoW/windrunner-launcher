using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;

namespace WindrunnerLauncher.App.Views.Dialogs;

public sealed partial class ChangeGmLevelDialog : Window
{
    public ChangeGmLevelDialog() => AvaloniaXamlLoader.Load(this);

    private void CancelClicked(object? sender, RoutedEventArgs e) => Close(null);

    private void SaveClicked(object? sender, RoutedEventArgs e)
    {
        var username = this.FindControl<TextBox>("UsernameBox")!.Text?.Trim() ?? "";
        var level = this.FindControl<ComboBox>("GmBox")!.SelectedIndex;
        if (username.Length < 2 || level is < 0 or > 3)
            return;
        Close(new GmLevelChange(username, level));
    }
}

public sealed record GmLevelChange(string Username, int GmLevel);
