using System.Collections.ObjectModel;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using WindrunnerLauncher.Core.Models;
using WindrunnerLauncher.Core.Mods;

namespace WindrunnerLauncher.App.ViewModels;

public sealed partial class AddonsViewModel : RuntimeViewModel, IDisposable
{
    private readonly AddonManager _manager;
    private readonly List<AddonItemViewModel> _all = [];
    private readonly CancellationTokenSource _lifetime = new();
    private bool _disposed;

    public AddonsViewModel(MainViewModel main) : base(main)
    {
        _manager = main.Runtime.Addons;
        Items = [];
        SyncFromManager();
        _manager.Changed += ManagerChanged;
        _ = RefreshUpdatesAsync();
    }

    private string ClientDir => Main.Runtime.Mods.ClientDirFor(Main.Runtime.State.SelectedRealm());

    public ObservableCollection<AddonItemViewModel> Items { get; }
    public bool HasItems => Items.Count > 0;
    public bool NoItems => !HasItems;
    public int InstalledCount => _all.Count(a => a.Installed);
    public int UpdateCount => _all.Count(a => a.HasUpdate);
    public string SummaryText => UpdateCount > 0
        ? $"{InstalledCount} of {_all.Count} installed · {UpdateCount} update(s) available"
        : $"{InstalledCount} of {_all.Count} installed";

    [ObservableProperty]
    private string _searchText = "";

    [ObservableProperty]
    private string _newAddonUrl = "";

    [ObservableProperty]
    private string _newAddonFilter = "";

    [ObservableProperty]
    private string? _addAddonError;

    [ObservableProperty]
    private bool _isAddingAddon;

    public bool HasAddAddonError => !string.IsNullOrWhiteSpace(AddAddonError);

    partial void OnSearchTextChanged(string value) => ApplyFilter();

    partial void OnAddAddonErrorChanged(string? value) => OnPropertyChanged(nameof(HasAddAddonError));

    internal void NotifySummaryChanged()
    {
        OnPropertyChanged(nameof(InstalledCount));
        OnPropertyChanged(nameof(UpdateCount));
        OnPropertyChanged(nameof(SummaryText));
    }

    [RelayCommand]
    private async Task AddAddon()
    {
        IsAddingAddon = true;
        AddAddonError = null;
        try
        {
            await _manager.AddFromUrlAsync(NewAddonUrl, NewAddonFilter, _lifetime.Token);
            SyncFromManager();
            NewAddonUrl = "";
            NewAddonFilter = "";
            ApplyFilter();
            NotifySummaryChanged();
        }
        catch (Exception ex)
        {
            AddAddonError = ex.Message;
        }
        finally
        {
            IsAddingAddon = false;
        }
    }

    [RelayCommand]
    private Task Refresh() => RefreshUpdatesAsync();

    internal Task InstallOrUpdateAsync(AddonItemViewModel item) =>
        _manager.InstallOrUpdateAsync(item.Entry, ClientDir, _lifetime.Token);

    internal Task ToggleAsync(AddonItemViewModel item, bool enabled)
    {
        _manager.SetEnabled(item.Entry, ClientDir, enabled);
        return Task.CompletedTask;
    }

    internal void Remove(AddonItemViewModel item)
    {
        _manager.Remove(item.Id, ClientDir);
        SyncFromManager();
    }

    private async Task RefreshUpdatesAsync()
    {
        if (_disposed)
            return;
        var ct = _lifetime.Token;
        foreach (var item in _all.Where(i => i.HasGitHubSource).ToList())
        {
            try
            {
                await _manager.RefreshLatestVersionAsync(item.Entry, ct);
                item.SyncFromEntry();
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return;
            }
            catch
            {
                // Install/Update reports errors; an unavailable release check keeps the saved version.
            }
        }

        NotifySummaryChanged();
    }

    private void ApplyFilter()
    {
        Items.Clear();
        var query = SearchText.Trim();
        foreach (var item in _all)
        {
            if (query.Length == 0 ||
                item.Name.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                item.Category.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                item.Author.Contains(query, StringComparison.OrdinalIgnoreCase))
                Items.Add(item);
        }

        OnPropertyChanged(nameof(HasItems));
        OnPropertyChanged(nameof(NoItems));
    }

    private void ManagerChanged()
    {
        if (Dispatcher.UIThread.CheckAccess())
            SyncFromManager();
        else
            Dispatcher.UIThread.Post(SyncFromManager);
    }

    private void SyncFromManager()
    {
        if (_disposed)
            return;

        var ids = _manager.Addons.Select(entry => entry.Id).ToHashSet(StringComparer.Ordinal);
        _all.RemoveAll(item => !ids.Contains(item.Id));
        foreach (var entry in _manager.Addons)
        {
            var item = _all.FirstOrDefault(item => item.Id == entry.Id);
            if (item is null)
                _all.Add(new AddonItemViewModel(this, entry));
            else
                item.SyncFromEntry();
        }

        ApplyFilter();
        NotifySummaryChanged();
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _manager.Changed -= ManagerChanged;
        _lifetime.Cancel();
        _lifetime.Dispose();
    }
}

public sealed partial class AddonItemViewModel : ObservableObject
{
    private readonly AddonsViewModel _owner;

    public AddonItemViewModel(AddonsViewModel owner, AddonEntry entry)
    {
        _owner = owner;
        Entry = entry;
        SyncFromEntry();
    }

    public AddonEntry Entry { get; }

    public string Id => Entry.Id;
    public string Name => Entry.Name;
    public string Author => Entry.Author;
    public string AuthorLabel => string.IsNullOrWhiteSpace(Author) ? "" : $"by {Author}";
    public string Category => Entry.Category;
    public string Description => Entry.Description;
    public bool HasGitHubSource => !string.IsNullOrWhiteSpace(Entry.GitHubRepo);
    public string SourceLabel => HasGitHubSource
        ? Entry.InstallAllMatchingAssets
            ? $"{Entry.GitHubRepo} ({(string.IsNullOrWhiteSpace(Entry.AssetFilter) ? "all ZIP assets" : $"all {Entry.AssetFilter} assets")})"
            : string.IsNullOrWhiteSpace(Entry.AssetFilter) ? Entry.GitHubRepo : $"{Entry.GitHubRepo} ({Entry.AssetFilter})"
        : Entry.Url;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StatusLabel))]
    private string _version = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanToggle))]
    [NotifyPropertyChangedFor(nameof(IsEnableAction))]
    [NotifyPropertyChangedFor(nameof(IsDisableAction))]
    [NotifyPropertyChangedFor(nameof(StatusLabel))]
    [NotifyPropertyChangedFor(nameof(ActionLabel))]
    [NotifyPropertyChangedFor(nameof(CanRunAction))]
    [NotifyPropertyChangedFor(nameof(IsUpdateAction))]
    private bool _installed;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ToggleLabel))]
    [NotifyPropertyChangedFor(nameof(IsEnableAction))]
    [NotifyPropertyChangedFor(nameof(IsDisableAction))]
    [NotifyPropertyChangedFor(nameof(StatusLabel))]
    private bool _enabled;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StatusLabel))]
    [NotifyPropertyChangedFor(nameof(ActionLabel))]
    [NotifyPropertyChangedFor(nameof(IsUpdateAction))]
    [NotifyPropertyChangedFor(nameof(CanRunAction))]
    private bool _hasUpdate;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanToggle))]
    [NotifyPropertyChangedFor(nameof(CanRunAction))]
    [NotifyPropertyChangedFor(nameof(ActionLabel))]
    [NotifyPropertyChangedFor(nameof(IsEnableAction))]
    [NotifyPropertyChangedFor(nameof(IsDisableAction))]
    [NotifyCanExecuteChangedFor(nameof(RemoveCommand))]
    private bool _isBusy;

    [ObservableProperty]
    private string? _errorText;

    public bool HasErrorText => !string.IsNullOrWhiteSpace(ErrorText);

    partial void OnErrorTextChanged(string? value) => OnPropertyChanged(nameof(HasErrorText));

    public bool CanToggle => Installed && !IsBusy;
    public string ToggleLabel => Enabled ? "Disable" : "Enable";
    public bool IsEnableAction => CanToggle && !Enabled;
    public bool IsDisableAction => CanToggle && Enabled;
    public bool IsUpdateAction => Installed && HasUpdate;
    public bool CanRunAction => !IsBusy && (!Installed || HasUpdate);
    public string ActionLabel => IsBusy ? "Working…" : !Installed ? "Install" : HasUpdate ? "Update" : "Installed";
    public string StatusLabel => !Installed ? "Not installed"
        : HasUpdate ? $"Update available ({Version})"
        : Enabled ? "Enabled" : "Disabled";

    internal void SyncFromEntry()
    {
        Version = Entry.Version;
        Installed = Entry.Installed;
        Enabled = Entry.Enabled;
        HasUpdate = Entry.Installed && !string.Equals(Entry.InstalledVersion, Entry.Version, StringComparison.OrdinalIgnoreCase);
    }

    [RelayCommand]
    private async Task InstallOrUpdateAsync()
    {
        if (!CanRunAction)
            return;
        IsBusy = true;
        ErrorText = null;
        try
        {
            await _owner.InstallOrUpdateAsync(this);
            SyncFromEntry();
        }
        catch (Exception ex)
        {
            ErrorText = ex.Message;
        }
        finally
        {
            IsBusy = false;
            _owner.NotifySummaryChanged();
        }
    }

    [RelayCommand]
    private async Task Toggle()
    {
        if (!CanToggle)
            return;
        IsBusy = true;
        ErrorText = null;
        try
        {
            await _owner.ToggleAsync(this, !Enabled);
            SyncFromEntry();
        }
        catch (Exception ex)
        {
            ErrorText = ex.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }

    private bool CanRemove => !IsBusy;

    [RelayCommand(CanExecute = nameof(CanRemove))]
    private void Remove() => _owner.Remove(this);
}
