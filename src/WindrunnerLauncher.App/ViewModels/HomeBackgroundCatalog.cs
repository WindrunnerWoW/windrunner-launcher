using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace WindrunnerLauncher.App.ViewModels;

internal static class HomeBackgroundCatalog
{
    public const string Prefix = "bundled:";
    public const string DefaultId = "default";
    public const string DefaultAssetUri = "avares://WindrunnerLauncher/Assets/Backgrounds/home_realm_v2.png";

    public static readonly HomeBackgroundDefinition[] Bundled =
    [
        new(DefaultId, "Stormwind", DefaultAssetUri),
        new("thunder-bluff", "Thunder Bluff", "avares://WindrunnerLauncher/Assets/Backgrounds/thunder_bluff.webp"),
        new("orgrimmar", "Orgrimmar", "avares://WindrunnerLauncher/Assets/Backgrounds/orgrimmar.webp"),
        new("undercity", "Undercity", "avares://WindrunnerLauncher/Assets/Backgrounds/undercity.webp"),
        new("ironforge", "Ironforge", "avares://WindrunnerLauncher/Assets/Backgrounds/ironforge.webp"),
        new("darnassus", "Darnassus", "avares://WindrunnerLauncher/Assets/Backgrounds/darnassus.webp")
    ];

    public static string? ToStoredValue(string id) =>
        string.Equals(id, DefaultId, StringComparison.Ordinal) ? null : Prefix + id;

    public static bool IsBundled(string? stored) =>
        string.IsNullOrWhiteSpace(stored) || stored.StartsWith(Prefix, StringComparison.Ordinal);

    public static string? SelectedId(string? stored)
    {
        if (string.IsNullOrWhiteSpace(stored))
            return DefaultId;
        if (stored.StartsWith(Prefix, StringComparison.Ordinal))
            return stored[Prefix.Length..];
        return null;
    }

    public static string DisplayName(string? stored)
    {
        var id = SelectedId(stored);
        if (id is null)
            return stored ?? "";

        foreach (var item in Bundled)
        {
            if (!string.Equals(item.Id, id, StringComparison.Ordinal))
                continue;
            return id == DefaultId ? "Windrunner (default)" : item.Title;
        }

        return "Windrunner (default)";
    }

    public static string AssetUri(string? stored)
    {
        var id = SelectedId(stored) ?? DefaultId;
        foreach (var item in Bundled)
        {
            if (string.Equals(item.Id, id, StringComparison.Ordinal))
                return item.AssetUri;
        }

        return DefaultAssetUri;
    }

    public static IImage Load(string assetUri)
    {
        using var stream = AssetLoader.Open(new Uri(assetUri));
        return new Bitmap(stream);
    }
}

internal readonly record struct HomeBackgroundDefinition(string Id, string Title, string AssetUri);

public sealed class BackgroundOptionViewModel : ObservableObject
{
    private bool _isSelected;

    public BackgroundOptionViewModel(string id, string title, IImage preview, Action<BackgroundOptionViewModel> select)
    {
        Id = id;
        Title = title;
        Preview = preview;
        SelectCommand = new RelayCommand(() => select(this));
    }

    public string Id { get; }
    public string Title { get; }
    public IImage Preview { get; }
    public IRelayCommand SelectCommand { get; }

    public bool IsSelected
    {
        get => _isSelected;
        set => SetProperty(ref _isSelected, value);
    }
}
