using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using WindrunnerLauncher.Core;
using WindrunnerLauncher.Core.Server;

namespace WindrunnerLauncher.App.ViewModels;

/// <summary>
/// Backs the server configuration dialog: the three ports, the player-bot counts, and the curated
/// conf keys from <see cref="ServerConfigCatalog"/>. Saving writes the friendly settings to
/// <c>portable.local.env</c> and the conf overrides into the live conf files.
/// </summary>
public sealed partial class ServerConfigViewModel : ObservableObject
{
    private readonly LauncherRuntime _runtime;

    /// <summary>Value each entry showed when the dialog opened, so only real edits become overrides.</summary>
    private readonly Dictionary<string, string> _loaded = new(StringComparer.Ordinal);

    public ServerConfigViewModel(LauncherRuntime runtime)
    {
        _runtime = runtime ?? throw new ArgumentNullException(nameof(runtime));

        var settings = runtime.State.Settings.Server;
        _mysqlPort = settings.MysqlPort;
        _authPort = settings.AuthPort;
        _worldPort = settings.WorldPort;

        BuildGroups();
    }

    public event Action? Saved;
    public event Action? Cancelled;

    public ObservableCollection<ServerConfigGroupViewModel> Groups { get; } = [];

    [ObservableProperty]
    private int _mysqlPort;

    [ObservableProperty]
    private int _authPort;

    [ObservableProperty]
    private int _worldPort;

    [RelayCommand]
    private void Save()
    {
        var settings = _runtime.State.Settings;
        settings.Server.MysqlPort = MysqlPort;
        settings.Server.AuthPort = AuthPort;
        settings.Server.WorldPort = WorldPort;

        var tuning = settings.ServerTuning;
        foreach (var item in Groups.SelectMany(g => g.Entries))
        {
            var value = item.SerializedValue;
            // Untouched entries keep whatever override they already had; only real edits are stored.
            if (!string.Equals(value, _loaded[item.Entry.Id], StringComparison.Ordinal))
                tuning[item.Entry.Id] = value;
        }

        _runtime.State.SaveSettings();
        _runtime.Realms.SyncLocalFromServerSettings();
        // Writes portable.local.env, re-patches the confs, and flags a restart when the realm is live.
        _runtime.Server.ApplyFriendlySettings();
        _runtime.Notify();
        Saved?.Invoke();
    }

    /// <summary>Drops every conf override and restores the shipped values from the dist files.</summary>
    [RelayCommand]
    private void Reset()
    {
        _runtime.State.Settings.ServerTuning.Clear();
        _runtime.State.SaveSettings();
        _runtime.Server.ApplyFriendlySettings();
        _runtime.Notify();
        BuildGroups();
    }

    [RelayCommand]
    private void Cancel() => Cancelled?.Invoke();

    private void BuildGroups()
    {
        _loaded.Clear();
        Groups.Clear();
        foreach (var (id, title) in ServerConfigCatalog.Groups)
        {
            var entries = ServerConfigCatalog.Entries
                .Where(e => e.Group == id)
                .Select(CreateEntry)
                .ToList();
            if (entries.Count > 0)
                Groups.Add(new ServerConfigGroupViewModel(title, entries));
        }
    }

    private ServerConfigEntryViewModel CreateEntry(ServerConfigEntry entry)
    {
        var path = Path.Combine(_runtime.Paths.ServerBinaries, entry.File);
        var stored = _runtime.State.Settings.ServerTuning.TryGetValue(entry.Id, out var value)
                     && !string.IsNullOrWhiteSpace(value)
            ? value
            : null;
        var raw = stored ?? ServerConfigFile.Read(path, entry.Section, entry.Key) ?? entry.Default;

        var item = new ServerConfigEntryViewModel(entry, raw);
        _loaded[entry.Id] = item.SerializedValue;
        return item;
    }
}

public sealed class ServerConfigGroupViewModel(string title, IReadOnlyList<ServerConfigEntryViewModel> entries)
{
    public string Title { get; } = title;
    public IReadOnlyList<ServerConfigEntryViewModel> Entries { get; } = entries;
}

/// <summary>One editable conf key. Numbers use <see cref="Number"/>, toggles use <see cref="Toggle"/>.</summary>
public sealed class ServerConfigEntryViewModel : ObservableObject
{
    private decimal? _number;
    private bool _toggle;

    public ServerConfigEntryViewModel(ServerConfigEntry entry, string raw)
    {
        Entry = entry;
        IsToggle = entry.Kind == ServerConfigKind.Toggle;
        IsNumber = !IsToggle;
        if (IsToggle)
            _toggle = raw is "1" or "true" or "True";
        else
            _number = decimal.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed)
                ? parsed
                : entry.Min;
    }

    public ServerConfigEntry Entry { get; }
    public string Label => Entry.Label;
    public string Hint => Entry.Hint;
    public bool IsNumber { get; }
    public bool IsToggle { get; }
    public string Format => Entry.Format;
    public decimal? Min => Entry.Min;
    public decimal? Max => Entry.Max;
    public decimal? Step => Entry.Step;

    public decimal? Number
    {
        get => _number;
        set
        {
            // An emptied box reports null; keep the last value instead of writing a surprise number.
            if (value is not null)
                SetProperty(ref _number, value);
        }
    }

    public bool Toggle
    {
        get => _toggle;
        set => SetProperty(ref _toggle, value);
    }

    /// <summary>The value as it is written into the conf file.</summary>
    public string SerializedValue => IsToggle
        ? (Toggle ? "1" : "0")
        : (Number ?? Entry.Min).ToString(Format, CultureInfo.InvariantCulture);
}
