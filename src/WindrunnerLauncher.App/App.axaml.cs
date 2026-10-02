using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Styling;
using WindrunnerLauncher.App.ViewModels;
using WindrunnerLauncher.App.Views;
using WindrunnerLauncher.Core;

namespace WindrunnerLauncher.App;

public sealed partial class App : Application
{
    private LauncherRuntime? _runtime;

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;
            _runtime = new LauncherRuntime();
#if DEBUG
            // Debug aid: `--preview-server-update` announces a made-up server release so the
            // update hint, dialog, and progress can be seen without publishing anything.
            if (desktop.Args?.Contains("--preview-server-update") == true)
                _runtime.Updates.BeginServerUpdatePreview();
#endif
            RequestedThemeVariant = _runtime.State.Settings.LightMode ? ThemeVariant.Light : ThemeVariant.Dark;
            var viewModel = new MainViewModel(_runtime);
            desktop.MainWindow = new MainWindow
            {
                DataContext = viewModel
            };
            desktop.Exit += (_, _) =>
            {
                viewModel.Dispose();
                _runtime?.Dispose();
                _runtime = null;
            };
        }

        base.OnFrameworkInitializationCompleted();
    }
}
