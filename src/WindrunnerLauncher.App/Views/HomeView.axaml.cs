using System.Diagnostics;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using WindrunnerLauncher.App.ViewModels;
using WindrunnerLauncher.App.Views.Dialogs;

namespace WindrunnerLauncher.App.Views;

public sealed partial class HomeView : UserControl
{
    public HomeView() => AvaloniaXamlLoader.Load(this);

    private void ViewAllClicked(object? sender, RoutedEventArgs e) =>
        OpenNewsUrl("https://windrunnerwow.github.io/news");

    private async void NewsItemClicked(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button { DataContext: NewsItem item } ||
            DataContext is not HomeViewModel vm ||
            TopLevel.GetTopLevel(this) is not Window owner)
            return;

        var dialog = new NewsPreviewDialog(item, vm.Texts["home.news.preview.readmore"], vm.Texts["home.news.preview.close"]);
        await dialog.ShowDialog(owner);
    }

    private static void OpenNewsUrl(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) ||
            uri.Scheme != Uri.UriSchemeHttps ||
            !uri.Host.Equals("windrunnerwow.github.io", StringComparison.OrdinalIgnoreCase))
            return;

        using var process = Process.Start(new ProcessStartInfo(uri.AbsoluteUri)
        {
            UseShellExecute = true
        });
    }
}
