using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using WindrunnerLauncher.App.ViewModels;
using WindrunnerLauncher.App.Views.Dialogs;

namespace WindrunnerLauncher.App.Views;

public sealed partial class ServerView : UserControl
{
    private ServerViewModel? _attachedViewModel;

    public ServerView()
    {
        AvaloniaXamlLoader.Load(this);
        DataContextChanged += (_, _) => Attach();
        AttachedToVisualTree += (_, _) => Attach();
        DetachedFromVisualTree += (_, _) => Detach();
    }

    private void Attach()
    {
        Detach();
        if (DataContext is not ServerViewModel vm)
            return;
        _attachedViewModel = vm;
        vm.ForceStopRequested += ConfirmForceStop;
        vm.CreateAccountRequested += CreateAccount;
        vm.ChangePasswordRequested += ChangePassword;
        vm.ChangeGmLevelRequested += ChangeGmLevel;
    }

    private void Detach()
    {
        if (_attachedViewModel is not { } vm)
            return;
        vm.ForceStopRequested -= ConfirmForceStop;
        vm.CreateAccountRequested -= CreateAccount;
        vm.ChangePasswordRequested -= ChangePassword;
        vm.ChangeGmLevelRequested -= ChangeGmLevel;
        _attachedViewModel = null;
    }

    private async void ConfirmForceStop()
    {
        if (DataContext is not ServerViewModel vm || TopLevel.GetTopLevel(this) is not Window owner)
            return;
        if (await new ForceStopDialog().ShowDialog<bool>(owner))
            await vm.ForceStopConfirmedAsync();
    }

    private async void CreateAccount()
    {
        if (DataContext is not ServerViewModel vm || TopLevel.GetTopLevel(this) is not Window owner)
            return;
        var result = await new CreateAccountDialog().ShowDialog<AccountCredentials?>(owner);
        if (result is not null)
            await vm.CreateAccountAsync(result.Username, result.Password);
    }

    private async void ChangePassword()
    {
        if (DataContext is not ServerViewModel vm || TopLevel.GetTopLevel(this) is not Window owner)
            return;
        var result = await new ChangePasswordDialog().ShowDialog<AccountCredentials?>(owner);
        if (result is not null)
            await vm.ChangePasswordAsync(result.Username, result.Password);
    }

    private async void ChangeGmLevel()
    {
        if (DataContext is not ServerViewModel vm || TopLevel.GetTopLevel(this) is not Window owner)
            return;
        var result = await new ChangeGmLevelDialog().ShowDialog<GmLevelChange?>(owner);
        if (result is not null)
            await vm.ChangeGmLevelAsync(result.Username, result.GmLevel);
    }
}
