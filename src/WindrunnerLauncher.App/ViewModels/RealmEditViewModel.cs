using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using WindrunnerLauncher.Core.Client;
using WindrunnerLauncher.Core.Models;

namespace WindrunnerLauncher.App.ViewModels;

public sealed partial class RealmEditViewModel : ObservableObject
{
    private readonly RealmEntry? _existing;
    private readonly int _authPort;

    public RealmEditViewModel(RealmEntry? existing = null)
    {
        _existing = existing;
        _displayName = existing?.DisplayName ?? "";
        _authPort = existing?.AuthPort ?? RealmlistWriter.DefaultAuthPort;
        _address = FormatAddress(existing?.Address ?? "", _authPort);
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

    [ObservableProperty]
    private string? _clientDirectory;

    [ObservableProperty]
    private bool _clearWdb;

    [RelayCommand(CanExecute = nameof(CanSave))]
    private void Save()
    {
        var realm = _existing ?? new RealmEntry();
        realm.DisplayName = DisplayName.Trim();
        ParseHostAndPort(Address, _authPort, out var host, out var port);
        realm.Address = host;
        realm.AuthPort = port;
        realm.ClientDirectoryOverride = string.IsNullOrWhiteSpace(ClientDirectory) ? null : ClientDirectory.Trim();
        realm.ClearWdb = ClearWdb;
        Saved?.Invoke(realm);
    }

    [RelayCommand]
    private void Cancel() => Cancelled?.Invoke();

    private static string FormatAddress(string address, int authPort)
    {
        var host = (address ?? "").Trim();
        if (host.Length == 0)
            return "";
        return RealmlistWriter.FormatHost(host, authPort);
    }

    private static void ParseHostAndPort(string raw, int fallbackPort, out string host, out int port)
    {
        host = (raw ?? "").Trim();
        port = fallbackPort is > 0 and <= 65535 ? fallbackPort : RealmlistWriter.DefaultAuthPort;
        if (host.Length == 0)
            return;

        var lastColon = host.LastIndexOf(':');
        if (lastColon <= 0 || lastColon == host.Length - 1)
            return;
        if (host.IndexOf(':') != lastColon)
            return;
        if (!int.TryParse(host[(lastColon + 1)..], out var parsed) || parsed is <= 0 or > 65535)
            return;

        host = host[..lastColon];
        port = parsed;
    }
}
