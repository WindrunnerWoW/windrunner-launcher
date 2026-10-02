using CommunityToolkit.Mvvm.ComponentModel;
using WindrunnerLauncher.Core.Localization;

namespace WindrunnerLauncher.App.ViewModels;

public sealed class LocalizedStrings : ObservableObject
{
    private readonly Loc _loc;

    public LocalizedStrings(Loc loc) => _loc = loc;

    public string this[string key] => _loc[key];

    public string Format(string key, params object[] values) => _loc.Format(key, values);

    public void Refresh() => OnPropertyChanged("Item[]");
}

public abstract class RuntimeViewModel : ObservableObject
{
    protected RuntimeViewModel(MainViewModel main) => Main = main;

    protected MainViewModel Main { get; }
    public LocalizedStrings Texts => Main.Texts;
}
