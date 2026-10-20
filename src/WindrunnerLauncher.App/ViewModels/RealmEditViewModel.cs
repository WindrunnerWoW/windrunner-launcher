using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using WindrunnerLauncher.Core.Client;
using WindrunnerLauncher.Core.Models;

namespace WindrunnerLauncher.App.ViewModels;

public sealed partial class RealmEditViewModel : ObservableObject
{
    private readonly RealmEntry? _existing;
    private readonly int _defaultAuthPort;

    /// <param name="existing">The realm being edited, or null when adding one.</param>
    /// <param name="defaultAuthPort">
    /// The port the client uses when no override is enabled. The local server's auth port follows
    /// the server settings; remote realms fall back to the vanilla default.
    /// </param>
    public RealmEditViewModel(RealmEntry? existing = null, int defaultAuthPort = RealmlistWriter.DefaultAuthPort)
    {
        _existing = existing;
        _defaultAuthPort = defaultAuthPort is > 0 and <= 65535 ? defaultAuthPort : RealmlistWriter.DefaultAuthPort;

        _displayName = existing?.DisplayName ?? "";
        // Older entries could carry a ":port" suffix in the address; it moves to the port field.
        var (host, inlinePort) = RealmlistWriter.SplitHostAndPort(existing?.Address ?? "");
        _address = host;
        _authPort = inlinePort ?? existing?.AuthPort ?? _defaultAuthPort;
        _advanced = inlinePort is not null
                    || (existing?.AuthPortOverride ?? false)
                    || (existing is not null && existing.AuthPort != _defaultAuthPort);
        _clientDirectory = existing?.ClientDirectoryOverride;
        _clearWdb = existing?.ClearWdb ?? false;
    }

    public event Action<RealmEntry>? Saved;
    public event Action? Cancelled;
    public string DialogTitle => _existing is null ? "Add realm" : "Edit realm";
    public bool CanSave => !string.IsNullOrWhiteSpace(DisplayName) &&
                           !string.IsNullOrWhiteSpace(Address);

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SaveCommand))]
    [NotifyPropertyChangedFor(nameof(CanSave))]
    private string _displayName;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SaveCommand))]
    [NotifyPropertyChangedFor(nameof(CanSave))]
    private string _address;

    /// <summary>Reveals the client-side auth port. Off means "use the default port".</summary>
    [ObservableProperty]
    private bool _advanced;

    [ObservableProperty]
    private int _authPort;

    public string DefaultPortHint => $"Uses the default port {_defaultAuthPort}.";

    [ObservableProperty]
    private string? _clientDirectory;

    [ObservableProperty]
    private bool _clearWdb;

    [RelayCommand(CanExecute = nameof(CanSave))]
    private void Save()
    {
        var realm = _existing ?? new RealmEntry();
        realm.DisplayName = DisplayName.Trim();

        // An inline ":port" still wins, so addresses typed the old way keep working.
        var (host, inlinePort) = RealmlistWriter.SplitHostAndPort(Address);
        realm.Address = host;
        var port = inlinePort ?? (Advanced && AuthPort is > 0 and <= 65535 ? AuthPort : (int?)null);
        if (port is { } clientPort)
        {
            realm.AuthPort = clientPort;
            realm.AuthPortOverride = true;
        }
        else
        {
            realm.AuthPort = _defaultAuthPort;
            realm.AuthPortOverride = false;
        }

        realm.ClientDirectoryOverride = string.IsNullOrWhiteSpace(ClientDirectory) ? null : ClientDirectory.Trim();
        realm.ClearWdb = ClearWdb;
        Saved?.Invoke(realm);
    }

    [RelayCommand]
    private void Cancel() => Cancelled?.Invoke();
}
