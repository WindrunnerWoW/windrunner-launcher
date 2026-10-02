using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using WindrunnerLauncher.Core.Models;
using WindrunnerLauncher.Core.Mods;

namespace WindrunnerLauncher.App.ViewModels;

public sealed partial class ModsViewModel : RuntimeViewModel
{
    private readonly List<ClientMpqItemViewModel> _allMpqItems = [];
    private bool _showAllMpqs;

    public ModsViewModel(MainViewModel main) : base(main)
    {
        Items = [];
        MpqItems = [];
        Addons = new AddonsViewModel(main);
        Refresh();
    }

    public event Action? ModsToolsRequested;
    public event Action? AddonsRequested;

    public ObservableCollection<ModItemViewModel> Items { get; }
    public ObservableCollection<ClientMpqItemViewModel> MpqItems { get; }
    public AddonsViewModel Addons { get; }
    public bool HasMpqItems => MpqItems.Count > 0;
    public bool NoMpqItems => !HasMpqItems;
    public string ModsSummaryText => Items.Count == 0
        ? "No mods or tools detected"
        : $"{Items.Count(i => i.Enabled)} of {Items.Count} enabled";

    [RelayCommand]
    private void OpenModsTools() => ModsToolsRequested?.Invoke();

    [RelayCommand]
    private void OpenAddons() => AddonsRequested?.Invoke();

    internal void NotifyModsSummaryChanged() => OnPropertyChanged(nameof(ModsSummaryText));

    /// <summary>Off by default: only custom (non-Blizzard) Data MPQs are shown until the user asks for everything.</summary>
    public bool ShowAllMpqs
    {
        get => _showAllMpqs;
        set
        {
            if (_showAllMpqs == value)
                return;
            _showAllMpqs = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(MpqFilterLabel));
            ApplyMpqFilter();
        }
    }

    public string MpqFilterLabel => ShowAllMpqs ? "Windrunner only" : "Show all";
    public int HiddenDefaultMpqCount => _allMpqItems.Count(i => i.Required);
    public bool HasHiddenDefaultMpqs => !ShowAllMpqs && HiddenDefaultMpqCount > 0;
    public string HiddenDefaultMpqText => $"{HiddenDefaultMpqCount} default Blizzard archive(s) hidden.";
    public string EmptyMpqMessage => _allMpqItems.Count == 0
        ? "No MPQ files found in this client's Data folder."
        : "No custom MPQ files found — this client only has default Blizzard archives.";

    [RelayCommand]
    private void ToggleMpqFilter() => ShowAllMpqs = !ShowAllMpqs;

    public bool HasPatchUpdates => Main.ClientHasPatchUpdate;
    public string PatchUpdatesText => $"Update available: {Main.OutdatedPatchList}. Press Update to download.";
    public string RealmName => Main.Status.Realm?.DisplayName ?? Texts["realm.local"];
    public bool HasDownload => Main.HasDownload;
    public string DownloadText => Main.DownloadText;
    public double DownloadProgress => Main.DownloadProgress;
    public bool DownloadIndeterminate => Main.DownloadIndeterminate;
    public bool UseRecommendedPreset
    {
        get => Main.Runtime.State.Settings.VanillaTweaks.UseRecommendedPreset;
        set
        {
            if (value == Main.Runtime.State.Settings.VanillaTweaks.UseRecommendedPreset)
                return;
            Main.Runtime.State.Settings.VanillaTweaks.UseRecommendedPreset = value;
            Save();
            OnPropertyChanged();
        }
    }

    public bool LargeAddressAware
    {
        get => Main.Runtime.State.Settings.VanillaTweaks.LargeAddressAware;
        set { Main.Runtime.State.Settings.VanillaTweaks.LargeAddressAware = value; Save(); OnPropertyChanged(); }
    }

    public bool AlwaysAutoLoot
    {
        get => Main.Runtime.State.Settings.VanillaTweaks.AlwaysAutoLoot;
        set { Main.Runtime.State.Settings.VanillaTweaks.AlwaysAutoLoot = value; Save(); OnPropertyChanged(); }
    }

    public bool SoundInBackground
    {
        get => Main.Runtime.State.Settings.VanillaTweaks.SoundInBackground;
        set { Main.Runtime.State.Settings.VanillaTweaks.SoundInBackground = value; Save(); OnPropertyChanged(); }
    }

    public bool NameplateRangeTbc
    {
        get => Main.Runtime.State.Settings.VanillaTweaks.NameplateRangeTbc;
        set { Main.Runtime.State.Settings.VanillaTweaks.NameplateRangeTbc = value; Save(); OnPropertyChanged(); }
    }

    public bool MoreSoundChannels
    {
        get => Main.Runtime.State.Settings.VanillaTweaks.MoreSoundChannels;
        set { Main.Runtime.State.Settings.VanillaTweaks.MoreSoundChannels = value; Save(); OnPropertyChanged(); }
    }

    private void Save()
    {
        Main.Runtime.State.SaveSettings();
    }

    internal void Refresh()
    {
        OnPropertyChanged(nameof(RealmName));
        OnPropertyChanged(nameof(HasDownload));
        OnPropertyChanged(nameof(DownloadText));
        OnPropertyChanged(nameof(DownloadProgress));
        OnPropertyChanged(nameof(DownloadIndeterminate));
        OnPropertyChanged(nameof(HasPatchUpdates));
        OnPropertyChanged(nameof(PatchUpdatesText));
        var realm = Main.Runtime.State.SelectedRealm();
        var rows = Main.Runtime.Mods.ListItems(realm);
        Items.Clear();
        foreach (var row in rows)
            Items.Add(new ModItemViewModel(this, Main, realm, row));
        OnPropertyChanged(nameof(ModsSummaryText));

        _allMpqItems.Clear();
        var outdated = Main.Status.OutdatedClientPatches.ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var row in Main.Runtime.Mods.ListClientMpqs(realm))
            _allMpqItems.Add(new ClientMpqItemViewModel(Main, realm, row, outdated.Contains(row.FileName)));
        ApplyMpqFilter();
    }

    private void ApplyMpqFilter()
    {
        MpqItems.Clear();
        foreach (var item in _allMpqItems)
        {
            if (ShowAllMpqs || !item.Required)
                MpqItems.Add(item);
        }

        OnPropertyChanged(nameof(HasMpqItems));
        OnPropertyChanged(nameof(NoMpqItems));
        OnPropertyChanged(nameof(EmptyMpqMessage));
        OnPropertyChanged(nameof(HiddenDefaultMpqCount));
        OnPropertyChanged(nameof(HasHiddenDefaultMpqs));
        OnPropertyChanged(nameof(HiddenDefaultMpqText));
    }
}

public sealed partial class ClientMpqItemViewModel : ObservableObject
{
    private readonly MainViewModel _main;
    private readonly RealmEntry _realm;

    public ClientMpqItemViewModel(MainViewModel main, RealmEntry realm, ClientMpqItem row, bool updateAvailable = false)
    {
        UpdateAvailable = updateAvailable;
        _main = main;
        _realm = realm;
        FileName = row.FileName;
        Required = row.Required;
        Active = row.Active;
        _enabled = row.Enabled;
    }

    public string FileName { get; }
    public bool Required { get; }
    public bool Active { get; }
    public bool UpdateAvailable { get; }
    public bool CanToggle => !Required;
    public string KindLabel => Required ? "REQUIRED" : "OPTIONAL";
    public string ToggleLabel => Required ? "On" : Enabled ? "Disable" : "Enable";
    public bool IsEnableAction => CanToggle && !Enabled;
    public bool IsDisableAction => CanToggle && Enabled;
    public string AvailabilityLabel => UpdateAvailable
        ? $"Update available · {StateLabel}"
        : StateLabel;
    private string StateLabel => Enabled == Active
        ? Active ? "Active in Data" : "Stored in DisabledMPQs"
        : "Will switch on Play";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ToggleLabel))]
    [NotifyPropertyChangedFor(nameof(AvailabilityLabel))]
    [NotifyPropertyChangedFor(nameof(IsEnableAction))]
    [NotifyPropertyChangedFor(nameof(IsDisableAction))]
    private bool _enabled;

    [RelayCommand]
    private async Task ToggleAsync()
    {
        if (!CanToggle)
            return;
        var desired = !Enabled;
        if (await _main.RunGuardedAsync(() =>
                _main.Runtime.Mods.SetClientMpqEnabledAsync(_realm, FileName, desired)))
            Enabled = desired;
    }
}

public sealed partial class ModItemViewModel : ObservableObject
{
    private readonly MainViewModel _main;
    private readonly RealmEntry _realm;
    private readonly string _assetId;

    public ModItemViewModel(ModsViewModel options, MainViewModel main, RealmEntry realm, ModListItem row)
    {
        Options = options;
        _main = main;
        _realm = realm;
        _assetId = row.Asset.Id;
        Name = string.IsNullOrWhiteSpace(row.Asset.DisplayName) ? row.Asset.Id : row.Asset.DisplayName;
        Description = row.Unmanaged
            ? row.Asset.Destination
            : row.Asset.Description ?? $"{row.Asset.Kind} client asset";
        Unmanaged = row.Unmanaged;
        Required = row.Asset.Required;
        Supported = ModHandlers.IsSupported(row.Asset.Kind);
        Installed = row.Installed;
        HasDownloadSource = Supported && (!string.IsNullOrWhiteSpace(row.Asset.DownloadUrl)
            || !string.IsNullOrWhiteSpace(row.Asset.GitHubRepo));
        _enabled = row.Enabled;
    }

    public string Name { get; }
    public string Description { get; }
    public ModsViewModel Options { get; }
    public bool IsVanillaTweaks => _assetId == "vanilla_tweaks";
    public bool Unmanaged { get; }
    public bool Required { get; }
    public bool Supported { get; }
    public bool Installed { get; }
    public bool HasDownloadSource { get; }
    public bool CanDownload => !Unmanaged && !Installed && HasDownloadSource;
    public bool CanToggle => Supported && !Unmanaged && !Required && (Enabled || Installed);
    public string KindLabel => Unmanaged ? "UNMANAGED" : Required ? "REQUIRED" : "OPTIONAL";
    public string ToggleLabel => Enabled ? "Disable" : "Enable";
    public string DownloadLabel => Installed ? "Cached" : "Download";
    public bool IsEnableAction => CanToggle && !Enabled;
    public bool IsDisableAction => CanToggle && Enabled;
    public bool IsDownloadAction => CanDownload;
    public string AvailabilityLabel => Unmanaged ? "Unmanaged file"
        : !Supported ? "Not supported yet"
        : Enabled && Installed ? "Enabled"
        : Installed ? HasDownloadSource ? "Cached for offline use" : "Ready"
        : HasDownloadSource ? "Download before enabling" : "Add files to the mods folder";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ToggleLabel))]
    [NotifyPropertyChangedFor(nameof(CanToggle))]
    [NotifyPropertyChangedFor(nameof(AvailabilityLabel))]
    [NotifyPropertyChangedFor(nameof(IsEnableAction))]
    [NotifyPropertyChangedFor(nameof(IsDisableAction))]
    private bool _enabled;

    partial void OnEnabledChanged(bool value) => Options.NotifyModsSummaryChanged();

    [RelayCommand]
    private async Task DownloadAsync()
    {
        if (!CanDownload)
            return;
        await _main.RunGuardedAsync(() =>
            _main.Runtime.Mods.DownloadAssetAsync(_realm, _assetId));
    }

    [RelayCommand]
    private async Task ToggleAsync()
    {
        if (!CanToggle)
            return;
        var desired = !Enabled;
        if (await _main.RunGuardedAsync(() =>
                _main.Runtime.Mods.SetEnabledAsync(_realm, _assetId, desired)))
        {
            Enabled = desired;
        }
    }
}
