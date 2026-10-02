using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace WindrunnerLauncher.App.Controls;

public sealed class NavigationButton : Button
{
    public static readonly StyledProperty<bool> IsSelectedProperty =
        AvaloniaProperty.Register<NavigationButton, bool>(nameof(IsSelected));

    public static readonly StyledProperty<string> LabelProperty =
        AvaloniaProperty.Register<NavigationButton, string>(nameof(Label), "");

    static NavigationButton()
    {
        FocusableProperty.OverrideDefaultValue<NavigationButton>(false);
        BackgroundProperty.OverrideDefaultValue<NavigationButton>(Brushes.Transparent);
        BorderThicknessProperty.OverrideDefaultValue<NavigationButton>(new Thickness(0));
        PaddingProperty.OverrideDefaultValue<NavigationButton>(new Thickness(12, 4));
        CursorProperty.OverrideDefaultValue<NavigationButton>(new Avalonia.Input.Cursor(Avalonia.Input.StandardCursorType.Hand));
    }

    public bool IsSelected
    {
        get => GetValue(IsSelectedProperty);
        set => SetValue(IsSelectedProperty, value);
    }

    public string Label
    {
        get => GetValue(LabelProperty);
        set => SetValue(LabelProperty, value);
    }
}
