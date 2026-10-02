using Avalonia;
using Avalonia.Controls;

namespace WindrunnerLauncher.App.Controls;

public sealed class ParchmentPanel : ContentControl
{
    public static readonly StyledProperty<Thickness> ContentPaddingProperty =
        AvaloniaProperty.Register<ParchmentPanel, Thickness>(nameof(ContentPadding), new Thickness(22, 18));

    public Thickness ContentPadding
    {
        get => GetValue(ContentPaddingProperty);
        set => SetValue(ContentPaddingProperty, value);
    }

    public ParchmentPanel()
    {
    }
}
