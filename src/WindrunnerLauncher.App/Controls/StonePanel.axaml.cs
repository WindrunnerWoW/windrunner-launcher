using Avalonia;
using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace WindrunnerLauncher.App.Controls;

public sealed partial class StonePanel : UserControl
{
    public static readonly StyledProperty<string> HeaderProperty =
        AvaloniaProperty.Register<StonePanel, string>(nameof(Header), "");

    public static readonly StyledProperty<object?> HeaderContentProperty =
        AvaloniaProperty.Register<StonePanel, object?>(nameof(HeaderContent));

    public static readonly StyledProperty<object?> PanelContentProperty =
        AvaloniaProperty.Register<StonePanel, object?>(nameof(PanelContent));

    public string Header { get => GetValue(HeaderProperty); set => SetValue(HeaderProperty, value); }
    public object? HeaderContent { get => GetValue(HeaderContentProperty); set => SetValue(HeaderContentProperty, value); }
    public object? PanelContent { get => GetValue(PanelContentProperty); set => SetValue(PanelContentProperty, value); }

    public StonePanel() => AvaloniaXamlLoader.Load(this);
}
