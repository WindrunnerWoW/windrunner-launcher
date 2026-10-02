using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Media;

namespace WindrunnerLauncher.App.Controls;

public sealed class StatusIndicator : TemplatedControl
{
    public static readonly StyledProperty<IImage?> IconProperty =
        AvaloniaProperty.Register<StatusIndicator, IImage?>(nameof(Icon));

    public static readonly StyledProperty<string> LabelProperty =
        AvaloniaProperty.Register<StatusIndicator, string>(nameof(Label), "");

    public static readonly StyledProperty<string> StateTextProperty =
        AvaloniaProperty.Register<StatusIndicator, string>(nameof(StateText), "");

    public static readonly StyledProperty<bool> IsOkProperty =
        AvaloniaProperty.Register<StatusIndicator, bool>(nameof(IsOk));

    public static readonly StyledProperty<bool> IsWarnProperty =
        AvaloniaProperty.Register<StatusIndicator, bool>(nameof(IsWarn));

    public static readonly StyledProperty<bool> IsBadProperty =
        AvaloniaProperty.Register<StatusIndicator, bool>(nameof(IsBad));

    public static readonly StyledProperty<bool> IsMutedProperty =
        AvaloniaProperty.Register<StatusIndicator, bool>(nameof(IsMuted));

    public static readonly StyledProperty<bool> IsBusyProperty =
        AvaloniaProperty.Register<StatusIndicator, bool>(nameof(IsBusy));

    public static readonly StyledProperty<string?> DetailProperty =
        AvaloniaProperty.Register<StatusIndicator, string?>(nameof(Detail));

    public static readonly DirectProperty<StatusIndicator, bool> HasDetailProperty =
        AvaloniaProperty.RegisterDirect<StatusIndicator, bool>(nameof(HasDetail), o => o.HasDetail);

    static StatusIndicator()
    {
        DetailProperty.Changed.AddClassHandler<StatusIndicator>((c, _) =>
            c.HasDetail = !string.IsNullOrWhiteSpace(c.Detail));
    }

    private bool _hasDetail;

    public IImage? Icon { get => GetValue(IconProperty); set => SetValue(IconProperty, value); }
    public string Label { get => GetValue(LabelProperty); set => SetValue(LabelProperty, value); }
    public string StateText { get => GetValue(StateTextProperty); set => SetValue(StateTextProperty, value); }
    public bool IsOk { get => GetValue(IsOkProperty); set => SetValue(IsOkProperty, value); }
    public bool IsWarn { get => GetValue(IsWarnProperty); set => SetValue(IsWarnProperty, value); }
    public bool IsBad { get => GetValue(IsBadProperty); set => SetValue(IsBadProperty, value); }
    public bool IsMuted { get => GetValue(IsMutedProperty); set => SetValue(IsMutedProperty, value); }
    public bool IsBusy { get => GetValue(IsBusyProperty); set => SetValue(IsBusyProperty, value); }
    public string? Detail
    {
        get => GetValue(DetailProperty);
        set => SetValue(DetailProperty, value);
    }

    public bool HasDetail
    {
        get => _hasDetail;
        private set => SetAndRaise(HasDetailProperty, ref _hasDetail, value);
    }
}
