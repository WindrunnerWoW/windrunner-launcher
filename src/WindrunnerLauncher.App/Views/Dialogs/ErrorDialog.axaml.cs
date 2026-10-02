using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;

namespace WindrunnerLauncher.App.Views.Dialogs;

public sealed partial class ErrorDialog : Window
{
    public ErrorDialog() : this("Something went wrong.", "") { }

    public ErrorDialog(string title, string detail)
    {
        AvaloniaXamlLoader.Load(this);
        this.FindControl<TextBlock>("TitleText")!.Text = title;
        this.FindControl<TextBlock>("DetailText")!.Text = detail;
    }

    private void CloseClicked(object? sender, RoutedEventArgs e) => Close();
}
