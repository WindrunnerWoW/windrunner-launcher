using System.Globalization;
using System.Text.Json;
using WindrunnerLauncher.Core.Persistence;

namespace WindrunnerLauncher.Core.Localization;

public sealed class Loc
{
    private Dictionary<string, string> _strings = new(StringComparer.OrdinalIgnoreCase);
    private string? _dataI18n;
    private string? _exeI18n;
    public string Language { get; private set; } = "en";
    public IReadOnlyList<string> Available { get; private set; } = ["en"];

    public event Action? Changed;

    public Loc()
    {
        Load("en");
    }

    public void Discover(LauncherPaths paths, string? executableDirectory = null)
    {
        _dataI18n = Path.Combine(paths.Root, "i18n");
        _exeI18n = Path.Combine(executableDirectory ?? LauncherPaths.ExecutableDirectory(), "i18n");
        var extra = new List<string> { "en" };
        CollectLanguages(extra, _dataI18n);
        CollectLanguages(extra, _exeI18n);
        Available = extra;
    }

    private static void CollectLanguages(List<string> extra, string locDir)
    {
        if (!Directory.Exists(locDir))
            return;
        foreach (var file in Directory.EnumerateFiles(locDir, "*.json"))
        {
            var code = Path.GetFileNameWithoutExtension(file);
            if (!extra.Contains(code, StringComparer.OrdinalIgnoreCase))
                extra.Add(code);
        }
    }

    public void Load(string language)
    {
        var requested = string.IsNullOrWhiteSpace(language) ? "en" : language;
        var embedded = ReadEmbedded(requested);
        var resolved = embedded is not null || HasDiskPack(requested) ? requested : "en";
        var table = embedded ?? ReadEmbedded("en") ?? new Dictionary<string, string>();

        foreach (var file in DiskPacks(resolved))
            Overlay(table, file);

        Language = resolved;
        _strings = new Dictionary<string, string>(table, StringComparer.OrdinalIgnoreCase);
        Changed?.Invoke();
    }

    private Dictionary<string, string>? ReadEmbedded(string language)
    {
        try
        {
            var json = EmbeddedResources.ReadText($"{language}.json");
            return JsonSerializer.Deserialize(json, LauncherJsonContext.Default.DictionaryStringString);
        }
        catch (Exception ex) when (ex is FileNotFoundException or InvalidOperationException or JsonException)
        {
            return null;
        }
    }

    private bool HasDiskPack(string language) => DiskPacks(language).Any();

    /// <summary>Shipped packs beside the binary first, then the data directory so a local file wins.</summary>
    private IEnumerable<string> DiskPacks(string language)
    {
        foreach (var dir in new[] { _exeI18n, _dataI18n })
        {
            if (string.IsNullOrWhiteSpace(dir))
                continue;
            var path = Path.Combine(dir, language + ".json");
            if (File.Exists(path))
                yield return path;
        }
    }

    private static void Overlay(Dictionary<string, string> table, string path)
    {
        try
        {
            var extra = JsonSerializer.Deserialize(
                File.ReadAllText(path), LauncherJsonContext.Default.DictionaryStringString);
            if (extra is null)
                return;
            foreach (var pair in extra)
            {
                if (pair.Value is not null)
                    table[pair.Key] = pair.Value;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            // A bad pack must not stop startup. Embedded strings stay in place.
        }
    }

    public string this[string key] =>
        _strings.TryGetValue(key, out var v) ? v : key;

    public string Format(string key, params object[] args) =>
        string.Format(CultureInfo.CurrentCulture, this[key], args);
}
