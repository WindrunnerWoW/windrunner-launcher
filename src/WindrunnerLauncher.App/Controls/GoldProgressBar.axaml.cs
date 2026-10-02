using Avalonia;
using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace WindrunnerLauncher.App.Controls;

public sealed partial class GoldProgressBar : UserControl
{
    public static readonly StyledProperty<double> ValueProperty =
        AvaloniaProperty.Register<GoldProgressBar, double>(nameof(Value));

    public static readonly StyledProperty<bool> IsIndeterminateProperty =
        AvaloniaProperty.Register<GoldProgressBar, bool>(nameof(IsIndeterminate));

    public static readonly StyledProperty<string> CaptionProperty =
        AvaloniaProperty.Register<GoldProgressBar, string>(nameof(Caption), "");

    public double Value { get => GetValue(ValueProperty); set => SetValue(ValueProperty, value); }
    public bool IsIndeterminate { get => GetValue(IsIndeterminateProperty); set => SetValue(IsIndeterminateProperty, value); }
    public string Caption { get => GetValue(CaptionProperty); set => SetValue(CaptionProperty, value); }

    public GoldProgressBar() => AvaloniaXamlLoader.Load(this);
}
