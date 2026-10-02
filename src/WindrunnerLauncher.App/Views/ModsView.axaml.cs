using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using WindrunnerLauncher.App.ViewModels;
using WindrunnerLauncher.App.Views.Dialogs;

namespace WindrunnerLauncher.App.Views;

public sealed partial class ModsView : UserControl
{
    private ModsViewModel? _attachedViewModel;

    public ModsView()
    {
        AvaloniaXamlLoader.Load(this);
        DataContextChanged += (_, _) => Attach();
        AttachedToVisualTree += (_, _) => Attach();
        DetachedFromVisualTree += (_, _) => Detach();
    }

    private void Attach()
    {
        Detach();
        if (DataContext is not ModsViewModel vm)
            return;
        _attachedViewModel = vm;
        vm.ModsToolsRequested += ShowModsTools;
        vm.AddonsRequested += ShowAddons;
    }

    private void Detach()
    {
        if (_attachedViewModel is not { } vm)
            return;
        vm.ModsToolsRequested -= ShowModsTools;
        vm.AddonsRequested -= ShowAddons;
        _attachedViewModel = null;
    }

    private async void ShowModsTools()
    {
        if (DataContext is not ModsViewModel vm || TopLevel.GetTopLevel(this) is not Window owner)
            return;
        await new ModsToolsDialog { DataContext = vm }.ShowDialog(owner);
    }

    private async void ShowAddons()
    {
        if (DataContext is not ModsViewModel vm || TopLevel.GetTopLevel(this) is not Window owner)
            return;
        await new AddonsDialog { DataContext = vm.Addons }.ShowDialog(owner);
    }
}
