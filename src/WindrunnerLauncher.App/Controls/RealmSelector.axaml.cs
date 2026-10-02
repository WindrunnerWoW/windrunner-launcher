using System.Collections;
using System.Windows.Input;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Markup.Xaml;

namespace WindrunnerLauncher.App.Controls;

public sealed partial class RealmSelector : UserControl
{
    public static readonly StyledProperty<IEnumerable?> ItemsSourceProperty =
        AvaloniaProperty.Register<RealmSelector, IEnumerable?>(nameof(ItemsSource));

    public static readonly StyledProperty<object?> SelectedItemProperty =
        AvaloniaProperty.Register<RealmSelector, object?>(nameof(SelectedItem), defaultBindingMode: Avalonia.Data.BindingMode.TwoWay);

    public static readonly StyledProperty<string> HeaderProperty =
        AvaloniaProperty.Register<RealmSelector, string>(nameof(Header), "Realm");

    public static readonly StyledProperty<IDataTemplate?> ItemTemplateProperty =
        AvaloniaProperty.Register<RealmSelector, IDataTemplate?>(nameof(ItemTemplate));

    public static readonly StyledProperty<ICommand?> EditCommandProperty =
        AvaloniaProperty.Register<RealmSelector, ICommand?>(nameof(EditCommand));

    public static readonly StyledProperty<ICommand?> AddCommandProperty =
        AvaloniaProperty.Register<RealmSelector, ICommand?>(nameof(AddCommand));

    public static readonly StyledProperty<string> StateTextProperty =
        AvaloniaProperty.Register<RealmSelector, string>(nameof(StateText), "");

    public static readonly StyledProperty<bool> IsStateOkProperty =
        AvaloniaProperty.Register<RealmSelector, bool>(nameof(IsStateOk));

    public static readonly StyledProperty<bool> IsStateWarnProperty =
        AvaloniaProperty.Register<RealmSelector, bool>(nameof(IsStateWarn));

    public static readonly StyledProperty<bool> IsStateBadProperty =
        AvaloniaProperty.Register<RealmSelector, bool>(nameof(IsStateBad));

    public static readonly StyledProperty<bool> IsStateMutedProperty =
        AvaloniaProperty.Register<RealmSelector, bool>(nameof(IsStateMuted));

    public IEnumerable? ItemsSource { get => GetValue(ItemsSourceProperty); set => SetValue(ItemsSourceProperty, value); }
    public object? SelectedItem { get => GetValue(SelectedItemProperty); set => SetValue(SelectedItemProperty, value); }
    public string Header { get => GetValue(HeaderProperty); set => SetValue(HeaderProperty, value); }
    public IDataTemplate? ItemTemplate { get => GetValue(ItemTemplateProperty); set => SetValue(ItemTemplateProperty, value); }
    public ICommand? EditCommand { get => GetValue(EditCommandProperty); set => SetValue(EditCommandProperty, value); }
    public ICommand? AddCommand { get => GetValue(AddCommandProperty); set => SetValue(AddCommandProperty, value); }
    public string StateText { get => GetValue(StateTextProperty); set => SetValue(StateTextProperty, value); }
    public bool IsStateOk { get => GetValue(IsStateOkProperty); set => SetValue(IsStateOkProperty, value); }
    public bool IsStateWarn { get => GetValue(IsStateWarnProperty); set => SetValue(IsStateWarnProperty, value); }
    public bool IsStateBad { get => GetValue(IsStateBadProperty); set => SetValue(IsStateBadProperty, value); }
    public bool IsStateMuted { get => GetValue(IsStateMutedProperty); set => SetValue(IsStateMutedProperty, value); }

    public RealmSelector() => AvaloniaXamlLoader.Load(this);
}
