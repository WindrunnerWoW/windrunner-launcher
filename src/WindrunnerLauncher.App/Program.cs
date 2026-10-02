using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Fonts.Inter;
using WindrunnerLauncher.Core;

namespace WindrunnerLauncher.App;

internal static partial class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        try
        {
            NativeLibraryBootstrap.EnsureBesideExecutable();
            BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
        }
        catch (Exception ex)
        {
            TryWriteCrashLog(ex);
            TryShowCrashDialog(ex);
        }
    }

    private static void TryWriteCrashLog(Exception ex)
    {
        try
        {
            var dir = LauncherPaths.FromExecutable().Root;
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, "crash.log"), $"{DateTime.UtcNow:o}{Environment.NewLine}{ex}");
        }
        catch
        {
            // Best-effort only. A GUI build may have no console.
        }
    }

    private static void TryShowCrashDialog(Exception ex)
    {
#if !LINUX_LAUNCHER
        if (!OperatingSystem.IsWindows())
            return;
        try
        {
            MessageBoxW(0, $"{ex.GetType().FullName}{Environment.NewLine}{ex.Message}", "WindrunnerLauncher failed to start", 0x10);
        }
        catch
        {
            // Best-effort only.
        }
#endif
    }

#if !LINUX_LAUNCHER
    [LibraryImport("user32.dll", StringMarshalling = StringMarshalling.Utf16, EntryPoint = "MessageBoxW")]
    private static partial int MessageBoxW(nint hWnd, string lpText, string lpCaption, uint uType);
#endif

    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .With(new X11PlatformOptions { OverlayPopups = true })
            .With(new Win32PlatformOptions
            {
                CompositionMode =
                [
                    Win32CompositionMode.WinUIComposition,
                    Win32CompositionMode.DirectComposition,
                    Win32CompositionMode.RedirectionSurface
                ]
            })
            .WithInterFont()
            .LogToTrace();
}
