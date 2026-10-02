using System.Diagnostics;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using WindrunnerLauncher.App.ViewModels;
using WindrunnerLauncher.Core.News;

namespace WindrunnerLauncher.App.Views.Dialogs;

public sealed partial class NewsPreviewDialog : Window
{
    public NewsPreviewDialog() => AvaloniaXamlLoader.Load(this);

    public NewsPreviewDialog(NewsItem item, string readMoreLabel, string closeLabel) : this()
    {
        DataContext = item;
        Title = item.Headline;
        this.FindControl<Button>("ReadMoreButton")!.Content = readMoreLabel + "  ↗";
        this.FindControl<Button>("CloseButton")!.Content = closeLabel;

        this.FindControl<Border>("CategoryChip")!.IsVisible = item.Category.Length > 0;
        this.FindControl<TextBlock>("MetaText")!.Text = item.Author.Length > 0
            ? $"{item.Date}  ·  {item.Author}"
            : item.Date;
        this.FindControl<TextBlock>("SourceText")!.Text =
            Uri.TryCreate(item.Link, UriKind.Absolute, out var uri) ? uri.Host : "";

        var summary = this.FindControl<TextBlock>("SummaryText")!;
        summary.IsVisible = item.Summary.Length > 0 &&
            (item.Body.Count == 0 || !item.Body[0].Text.StartsWith(item.Summary, StringComparison.Ordinal));
        BuildArticle(this.FindControl<StackPanel>("ArticleBody")!, item.Body);
    }

    private static void BuildArticle(StackPanel host, IReadOnlyList<NewsArticleBlock> blocks)
    {
        foreach (var block in blocks)
        {
            Control control = block.Kind switch
            {
                NewsBlockKind.Heading => new TextBlock { Text = block.Text, Classes = { "news-article-heading" } },
                NewsBlockKind.Bullet => new Grid
                {
                    ColumnDefinitions = new ColumnDefinitions("18,*"),
                    Children =
                    {
                        new TextBlock { Text = "•", Classes = { "news-article-bullet" } },
                        new TextBlock { Text = block.Text, Classes = { "news-article-text" }, [Grid.ColumnProperty] = 1 }
                    }
                },
                NewsBlockKind.Quote => new Border
                {
                    Classes = { "news-article-quote" },
                    Child = new TextBlock { Text = block.Text, Classes = { "news-article-text" } }
                },
                _ => new TextBlock { Text = block.Text, Classes = { "news-article-text" } }
            };
            host.Children.Add(control);
        }
    }

    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);
        // SizeToContent re-measures the article after opening, which can leave the scroller mid-text.
        var scroll = this.FindControl<ScrollViewer>("ArticleScroll")!;
        Dispatcher.UIThread.Post(() => scroll.Offset = default, DispatcherPriority.Background);
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.Key == Key.Escape)
        {
            e.Handled = true;
            Close();
        }
    }

    private void DragAreaPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
            BeginMoveDrag(e);
    }

    private void CloseClicked(object? sender, RoutedEventArgs e) => Close();

    private void ReadMoreClicked(object? sender, RoutedEventArgs e)
    {
        if (DataContext is NewsItem item)
            OpenNewsUrl(item.Link);
        Close();
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
