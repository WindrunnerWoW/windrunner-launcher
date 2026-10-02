using System.Windows.Input;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace WindrunnerLauncher.App.Controls;

public sealed partial class PlayButton : UserControl
{
    public static readonly StyledProperty<string> TextProperty =
        AvaloniaProperty.Register<PlayButton, string>(nameof(Text), "PLAY");

    public static readonly StyledProperty<string?> HintProperty =
        AvaloniaProperty.Register<PlayButton, string?>(nameof(Hint));

    public static readonly StyledProperty<ICommand?> CommandProperty =
        AvaloniaProperty.Register<PlayButton, ICommand?>(nameof(Command));

    public static readonly StyledProperty<bool> IsPlayEnabledProperty =
        AvaloniaProperty.Register<PlayButton, bool>(nameof(IsPlayEnabled), true);

    public string Text
    {
        get => GetValue(TextProperty);
        set => SetValue(TextProperty, value);
    }

    public string? Hint { get => GetValue(HintProperty); set => SetValue(HintProperty, value); }
    public ICommand? Command { get => GetValue(CommandProperty); set => SetValue(CommandProperty, value); }
    public bool IsPlayEnabled { get => GetValue(IsPlayEnabledProperty); set => SetValue(IsPlayEnabledProperty, value); }
    public PlayButton()
    {
        AvaloniaXamlLoader.Load(this);
    }
}
