using WindrunnerLauncher.App.ViewModels;
using WindrunnerLauncher.Core.Models;
using Xunit;

namespace WindrunnerLauncher.App.Tests;

public class AddonItemViewModelTests
{
    [Theory]
    [InlineData(false, false, "Install")]
    [InlineData(true, false, "Installed")]
    [InlineData(true, true, "Update")]
    public void FinishingOperation_NotifiesActionLabel(bool installed, bool hasUpdate, string expectedLabel)
    {
        var item = new AddonItemViewModel(null!, new AddonEntry());
        item.IsBusy = true;
        item.Installed = installed;
        item.HasUpdate = hasUpdate;
        var changes = new List<string?>();
        item.PropertyChanged += (_, args) => changes.Add(args.PropertyName);

        item.IsBusy = false;

        Assert.Equal(expectedLabel, item.ActionLabel);
        Assert.Contains(nameof(item.ActionLabel), changes);
        Assert.Equal(!installed || hasUpdate, item.CanRunAction);
    }

    [Fact]
    public void InstallingAddon_RestoresToggleButtonsInExistingRow()
    {
        var item = new AddonItemViewModel(null!, new AddonEntry());
        item.IsBusy = true;
        item.Installed = true;
        item.Enabled = true;
        var changes = new List<string?>();
        item.PropertyChanged += (_, args) => changes.Add(args.PropertyName);

        item.IsBusy = false;

        Assert.True(item.CanToggle);
        Assert.True(item.IsDisableAction);
        Assert.False(item.IsEnableAction);
        Assert.Contains(nameof(item.CanToggle), changes);
        Assert.Contains(nameof(item.IsDisableAction), changes);
        Assert.Contains(nameof(item.IsEnableAction), changes);
    }

    [Fact]
    public void WorkingAddon_CannotBeRemoved_AndNotifiesWhenAvailable()
    {
        var item = new AddonItemViewModel(null!, new AddonEntry());
        var notifications = 0;
        item.RemoveCommand.CanExecuteChanged += (_, _) => notifications++;

        item.IsBusy = true;
        Assert.False(item.RemoveCommand.CanExecute(null));
        item.IsBusy = false;

        Assert.True(item.RemoveCommand.CanExecute(null));
        Assert.Equal(2, notifications);
    }
}
