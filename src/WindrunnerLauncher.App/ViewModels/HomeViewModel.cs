using System.Collections.ObjectModel;
using Avalonia.Threading;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using WindrunnerLauncher.Core.News;

namespace WindrunnerLauncher.App.ViewModels;

public sealed class NewsItem : ObservableObject
{
    private IImage _thumbnail = null!;

    public string Date { get; init; } = "";
    public string Headline { get; init; } = "";
    public string Summary { get; init; } = "";
    public string Category { get; init; } = "";
    public string Author { get; init; } = "";
    public IReadOnlyList<NewsArticleBlock> Body { get; init; } = [];
    public string Link { get; init; } = "https://windrunnerwow.github.io/news";
    public string ThumbnailUrl { get; init; } = "";
    public IImage Thumbnail
    {
        get => _thumbnail;
        set => SetProperty(ref _thumbnail, value);
    }
}

public sealed partial class HomeViewModel : RuntimeViewModel, IDisposable
{
    private const int MaxThumbnailBytes = 2_000_000;
    private static readonly HttpClient ThumbnailHttp = new(new HttpClientHandler { AllowAutoRedirect = false })
    {
        Timeout = TimeSpan.FromSeconds(10)
    };

    private static readonly IImage[] Thumbnails =
    [
        LoadThumb(0),
        LoadThumb(1),
        LoadThumb(2)
    ];

    private readonly Dictionary<string, IImage> _thumbnailCache = new(StringComparer.Ordinal);
    private readonly HashSet<string> _loadingThumbnailUrls = new(StringComparer.Ordinal);
    private readonly HashSet<string> _failedThumbnailUrls = new(StringComparer.Ordinal);
    private readonly CancellationTokenSource _lifetime = new();
    private bool _disposed;
    private IImage _backgroundImage = null!;
    private string? _backgroundSource;
    private object? _newsSource;
    private string? _newsLanguage;

    private static IImage LoadThumb(int index)
    {
        var name = index switch
        {
            1 => "news_v2_2.png",
            2 => "news_v2_3.png",
            _ => "news_v2_1.png"
        };
        using var stream = AssetLoader.Open(new Uri($"avares://WindrunnerLauncher/Assets/News/{name}"));
        return new Bitmap(stream);
    }

    public HomeViewModel(MainViewModel main) : base(main)
    {
        _backgroundImage = LoadBackgroundImage();
        RebuildNews();
    }

    public ObservableCollection<NewsItem> NewsItems { get; } = [];
    public IImage BackgroundImage => _backgroundImage;

    public string RealmName => Main.Status.Realm?.DisplayName ?? Texts["realm.local"];
    public string RealmAddress => Main.Status.Realm is null
        ? ""
        : $"{Main.Status.Realm.Address}:{Main.Status.Realm.AuthPort}";
    public string Welcome => Texts["home.welcome"];
    public string PatchNotes => string.IsNullOrWhiteSpace(Main.Status.PatchNotes)
        ? Texts["patchnotes.empty"]
        : Main.Status.PatchNotes!;
    public string ClientSummary => Main.ClientStateText;
    public string ServerSummary => Main.ServerIsOnline ? Texts["status.online"] : Main.ServerStateText;
    public string ModsSummary => Main.ModsStateText;
    public string UpdateSummary => Main.UpdateStateText;
    public string PlayLabel => Main.PlayLabel;
    public bool CanPlay => Main.CanPlay;
    public bool ClientIsReady => Main.ClientIsReady;
    public bool ClientChipReady => Main.ClientIsReady && !Main.ClientHasPatchUpdate;
    public bool ClientHasPatchUpdate => Main.ClientHasPatchUpdate;
    public bool ClientIsMissing => Main.ClientIsMissing;
    public bool ServerIsOnline => Main.ServerIsOnline;
    public bool ServerIsWarning => Main.ServerIsWarning;
    public bool ServerIsUnavailable => Main.ServerIsUnavailable;
    public bool ModsHasWarning => Main.ModsHasWarning;
    public bool ModsIsReady => Main.ModsIsReady;
    public bool UpdatesIsReady => Main.UpdatesIsReady;
    public bool UpdatesIsWarning => Main.UpdatesIsWarning;

    public IAsyncRelayCommand PlayCommand => Main.PlayCommand;
    public IRelayCommand EditRealmCommand => Main.EditRealmCommand;

    internal void Refresh()
    {
        if (_disposed)
            return;
        if (!string.Equals(_backgroundSource, BackgroundSourceKey(), StringComparison.Ordinal))
        {
            var previous = _backgroundImage;
            _backgroundImage = LoadBackgroundImage();
            OnPropertyChanged(nameof(BackgroundImage));
            (previous as IDisposable)?.Dispose();
        }
        OnPropertyChanged(nameof(RealmName));
        OnPropertyChanged(nameof(RealmAddress));
        OnPropertyChanged(nameof(Welcome));
        OnPropertyChanged(nameof(PatchNotes));
        OnPropertyChanged(nameof(ClientSummary));
        OnPropertyChanged(nameof(ServerSummary));
        OnPropertyChanged(nameof(ModsSummary));
        OnPropertyChanged(nameof(UpdateSummary));
        OnPropertyChanged(nameof(PlayLabel));
        OnPropertyChanged(nameof(CanPlay));
        OnPropertyChanged(nameof(ClientIsReady));
        OnPropertyChanged(nameof(ClientChipReady));
        OnPropertyChanged(nameof(ClientHasPatchUpdate));
        OnPropertyChanged(nameof(ClientIsMissing));
        OnPropertyChanged(nameof(ServerIsOnline));
        OnPropertyChanged(nameof(ServerIsWarning));
        OnPropertyChanged(nameof(ServerIsUnavailable));
        OnPropertyChanged(nameof(ModsHasWarning));
        OnPropertyChanged(nameof(ModsIsReady));
        OnPropertyChanged(nameof(UpdatesIsReady));
        OnPropertyChanged(nameof(UpdatesIsWarning));
        RebuildNews();
    }

    private string BackgroundSourceKey() => Main.Runtime.State.Settings.HomeBackgroundPath ?? "";

    private IImage LoadBackgroundImage()
    {
        _backgroundSource = BackgroundSourceKey();
        var stored = Main.Runtime.State.Settings.HomeBackgroundPath;
        if (!HomeBackgroundCatalog.IsBundled(stored) && !string.IsNullOrWhiteSpace(stored) && File.Exists(stored))
        {
            try
            {
                return new Bitmap(stored);
            }
            catch
            {
                // Fall back to bundled artwork if the selected file is unreadable.
            }
        }

        return HomeBackgroundCatalog.Load(HomeBackgroundCatalog.AssetUri(stored));
    }

    private void RebuildNews()
    {
        // Thumbnails update items in place, so the list only changes with the feed or language.
        var feedItems = Main.Runtime.News.Items;
        var language = Main.Runtime.Loc.Language;
        if (ReferenceEquals(_newsSource, feedItems) && _newsLanguage == language && NewsItems.Count > 0)
            return;
        _newsSource = feedItems;
        _newsLanguage = language;
        NewsItems.Clear();
        if (feedItems.Count > 0)
        {
            for (var feedIndex = 0; feedIndex < feedItems.Count; feedIndex++)
            {
                var item = feedItems[feedIndex];
                NewsItems.Add(new NewsItem
                {
                    Date = item.PublishedAt,
                    Headline = item.Title,
                    Summary = item.Description,
                    Category = item.Category,
                    Author = item.Author,
                    Body = item.Body,
                    Link = item.Link,
                    ThumbnailUrl = item.ThumbnailUrl,
                    Thumbnail = _thumbnailCache.TryGetValue(item.ThumbnailUrl, out var cached)
                        ? cached
                        : Thumbnails[feedIndex % Thumbnails.Length]
                });
                QueueThumbnail(item.ThumbnailUrl);
            }
            return;
        }

        NewsItems.Add(new NewsItem
        {
            Date = Texts["home.news.bulletin"],
            Headline = Texts["home.news.unavailable.title"],
            Summary = Texts["home.news.unavailable.body"],
            Thumbnail = Thumbnails[0]
        });
    }

    private void QueueThumbnail(string url)
    {
        if (_thumbnailCache.ContainsKey(url) || _loadingThumbnailUrls.Contains(url) ||
            _failedThumbnailUrls.Contains(url))
            return;

        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) ||
            uri.Scheme != Uri.UriSchemeHttps ||
            !uri.Host.Equals("windrunnerwow.github.io", StringComparison.OrdinalIgnoreCase))
            return;

        _loadingThumbnailUrls.Add(url);
        _ = LoadThumbnailAsync(url, uri);
    }

    private async Task LoadThumbnailAsync(string url, Uri uri)
    {
        byte[]? bytes = null;
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
            timeout.CancelAfter(TimeSpan.FromSeconds(10));
            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            using var response = await ThumbnailHttp.SendAsync(
                request, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            if (response.Content.Headers.ContentLength is > MaxThumbnailBytes ||
                response.Content.Headers.ContentType?.MediaType?.StartsWith("image/", StringComparison.OrdinalIgnoreCase) != true)
                return;

            using var source = await response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false);
            using var buffer = new MemoryStream();
            var chunk = new byte[81920];
            int read;
            while ((read = await source.ReadAsync(chunk, timeout.Token).ConfigureAwait(false)) > 0)
            {
                if (buffer.Length + read > MaxThumbnailBytes)
                    return;
                buffer.Write(chunk, 0, read);
            }
            bytes = buffer.ToArray();
        }
        catch (Exception)
        {
            // The bundled image remains visible when the network or decoder fails.
        }
        finally
        {
            Dispatcher.UIThread.Post(() =>
            {
                if (_disposed)
                    return;
                _loadingThumbnailUrls.Remove(url);
                if (bytes is null)
                {
                    _failedThumbnailUrls.Add(url);
                    return;
                }

                try
                {
                    using var stream = new MemoryStream(bytes);
                    var bitmap = new Bitmap(stream);
                    _thumbnailCache[url] = bitmap;
                    foreach (var item in NewsItems.Where(item => item.ThumbnailUrl == url))
                        item.Thumbnail = bitmap;
                }
                catch (Exception)
                {
                    _failedThumbnailUrls.Add(url);
                }
            });
        }
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _lifetime.Cancel();
        _lifetime.Dispose();
        (_backgroundImage as IDisposable)?.Dispose();
        foreach (var thumbnail in _thumbnailCache.Values.OfType<IDisposable>())
            thumbnail.Dispose();
        _thumbnailCache.Clear();
    }
}
