using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace WindrunnerLauncher.App.Controls;

public sealed class LauncherButton : Button
{
    static LauncherButton()
    {
        CursorProperty.OverrideDefaultValue<LauncherButton>(new Avalonia.Input.Cursor(Avalonia.Input.StandardCursorType.Hand));
    }
}
