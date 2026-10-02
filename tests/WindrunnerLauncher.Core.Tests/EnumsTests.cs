namespace WindrunnerLauncher.Core.Tests;

public class EnumsTests
{
    [Fact]
    public void ServerLifecycleState_HasAllPlannedStates()
    {
        var names = Enum.GetNames<ServerLifecycleState>();
        foreach (var expected in new[]
                 {
                     "NotInstalled", "Stopped", "StartingDatabase", "StartingAuth", "InitializingWorld",
                     "Ready", "Stopping", "RestartRequired", "Updating", "RollingBack", "Error"
                 })
        {
            Assert.Contains(expected, names);
        }

        Assert.Equal(11, names.Length);
        Assert.Equal(ServerLifecycleState.NotInstalled, default);
    }

    [Fact]
    public void ServerLifecycleState_OrderIsStable()
    {
        Assert.True((int)ServerLifecycleState.StartingDatabase < (int)ServerLifecycleState.StartingAuth);
        Assert.True((int)ServerLifecycleState.StartingAuth < (int)ServerLifecycleState.InitializingWorld);
        Assert.True((int)ServerLifecycleState.InitializingWorld < (int)ServerLifecycleState.Ready);
    }

    [Fact]
    public void PlayActionKind_HasAllActions()
    {
        var names = Enum.GetNames<PlayActionKind>();
        Assert.Equal(new[] { "Onboarding", "Play", "Update", "InstallServer", "RestartRequired" }, names);
    }

    [Fact]
    public void RealmEntry_ToString_IsDisplayName()
    {
        var realm = new RealmEntry { DisplayName = "Local Server", Address = "127.0.0.1" };
        Assert.Equal("Local Server", realm.ToString());
        realm.DisplayName = "";
        realm.Id = "abc";
        Assert.Equal("abc", realm.ToString());
    }

    [Fact]
    public void TrustDomain_HasThreeDomains()
    {
        Assert.Equal(new[] { "Launcher", "Client", "Server" }, Enum.GetNames<TrustDomain>());
    }

    [Fact]
    public void ModKind_CoversManagedAssetTypes()
    {
        var names = Enum.GetNames<ModKind>();
        foreach (var k in new[] { "Mpq", "Dll", "AddOn", "ExecutablePatch", "Configuration", "ZipRoot", "WdbBlock", "Glue", "Dxvk" })
            Assert.Contains(k, names);
    }

    [Fact]
    public void DownloadStatus_TerminalStates()
    {
        var names = Enum.GetNames<DownloadStatus>();
        foreach (var k in new[] { "Idle", "Running", "Cancelling", "RetryWait", "Succeeded", "Failed", "Cancelled" })
            Assert.Contains(k, names);
    }

    [Fact]
    public void InstallChoice_DefaultsToClientAndLocalServer()
    {
        var settings = new LauncherSettings();
        Assert.Equal(InstallChoice.ClientAndLocalServer, settings.InstallChoice);
        Assert.False(settings.ClientIsManagedCopy);
        Assert.Equal(new[] { "ClientOnly", "ClientAndLocalServer" }, Enum.GetNames<InstallChoice>());
    }

    [Fact]
    public void DownloadProgress_Fraction()
    {
        Assert.Null(new DownloadProgress { BytesReceived = 10, TotalBytes = null }.Fraction);
        Assert.Null(new DownloadProgress { BytesReceived = 10, TotalBytes = 0 }.Fraction);
        Assert.Equal(0.5, new DownloadProgress { BytesReceived = 50, TotalBytes = 100 }.Fraction);
    }
}
