using WindrunnerLauncher.App.ViewModels;
using WindrunnerLauncher.Core.Models;
using Xunit;

namespace WindrunnerLauncher.App.Tests;

public class RealmEditViewModelTests
{
    private static RealmEditViewModel Open(RealmEntry? existing, int defaultPort = 3724) =>
        new(existing, defaultPort);

    private static RealmEntry Save(RealmEditViewModel vm)
    {
        RealmEntry? saved = null;
        vm.Saved += realm => saved = realm;
        vm.SaveCommand.Execute(null);
        Assert.NotNull(saved);
        return saved!;
    }

    [Fact]
    public void ExistingRealm_ShowsHostOnly_AndKeepsPortBehindAdvanced()
    {
        var realm = new RealmEntry { DisplayName = "Remote", Address = "logon.example.org", AuthPort = 4000, AuthPortOverride = true };

        var vm = Open(realm);

        Assert.Equal("logon.example.org", vm.Address);
        Assert.True(vm.Advanced);
        Assert.Equal(4000, vm.AuthPort);
    }

    [Fact]
    public void LegacyInlinePort_MovesIntoAdvancedPort()
    {
        var realm = new RealmEntry { DisplayName = "Remote", Address = "logon.example.org:3308", AuthPort = 3724 };

        var vm = Open(realm);

        Assert.Equal("logon.example.org", vm.Address);
        Assert.True(vm.Advanced);
        Assert.Equal(3308, vm.AuthPort);
    }

    [Fact]
    public void NewRealm_DefaultsToDefaultPort_WithoutAdvanced()
    {
        var vm = Open(null, defaultPort: 3820);
        vm.DisplayName = "Fresh";
        vm.Address = "logon.example.org";

        var realm = Save(vm);

        Assert.Equal("logon.example.org", realm.Address);
        Assert.Equal(3820, realm.AuthPort);
        Assert.False(realm.AuthPortOverride);
    }

    [Fact]
    public void DisablingAdvanced_RemovesCustomClientPort()
    {
        var realm = new RealmEntry { DisplayName = "Local", Address = "127.0.0.1", AuthPort = 4000, AuthPortOverride = true };
        var vm = Open(realm, defaultPort: 3724);

        vm.Advanced = false;
        var saved = Save(vm);

        Assert.Equal("127.0.0.1", saved.Address);
        Assert.Equal(3724, saved.AuthPort);
        Assert.False(saved.AuthPortOverride);
    }

    [Fact]
    public void DisablingAdvanced_FallsBackToLocalServerPort()
    {
        var realm = new RealmEntry { DisplayName = "Local", Address = "127.0.0.1", AuthPort = 3725 };
        var vm = Open(realm, defaultPort: 3725);

        Assert.False(vm.Advanced);
        var saved = Save(vm);

        Assert.Equal(3725, saved.AuthPort);
        Assert.False(saved.AuthPortOverride);
    }

    [Fact]
    public void EnablingAdvanced_StoresClientPortOverride()
    {
        var realm = new RealmEntry { DisplayName = "Local", Address = "127.0.0.1", AuthPort = 3724 };
        var vm = Open(realm);

        vm.Advanced = true;
        vm.AuthPort = 4100;
        var saved = Save(vm);

        Assert.Equal(4100, saved.AuthPort);
        Assert.True(saved.AuthPortOverride);
    }

    [Fact]
    public void TypedInlinePort_StillWinsOverAdvancedField()
    {
        var realm = new RealmEntry { DisplayName = "Remote", Address = "logon.example.org", AuthPort = 4000, AuthPortOverride = true };
        var vm = Open(realm);

        vm.Address = "logon.example.org:5000";
        var saved = Save(vm);

        Assert.Equal("logon.example.org", saved.Address);
        Assert.Equal(5000, saved.AuthPort);
        Assert.True(saved.AuthPortOverride);
    }
}
