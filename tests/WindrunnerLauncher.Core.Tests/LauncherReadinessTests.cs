using WindrunnerLauncher.Core.Localization;

namespace WindrunnerLauncher.Core.Tests;

public class LauncherReadinessTests
{
    [Fact]
    public void NoClient_Wins_WhenClientIsMissing()
    {
        var status = Local(clientReady: false, ServerLifecycleState.Error);
        var result = LauncherReadinessResolver.Resolve(status);
        Assert.Equal(LauncherReadinessKind.NoClient, result.Kind);
        Assert.Equal("status.launcher.noclient", result.TextKey);
        Assert.False(result.IsBusy);
    }

    [Fact]
    public void Error_Wins_WhenClientExists()
    {
        var status = Local(clientReady: true, ServerLifecycleState.Error);
        var result = LauncherReadinessResolver.Resolve(status);
        Assert.Equal(LauncherReadinessKind.Error, result.Kind);
        Assert.Equal("status.launcher.error", result.TextKey);
    }

    [Fact]
    public void UpdateRequired_WhenClientUpdateIsBlocking()
    {
        var status = Local(clientReady: true, ServerLifecycleState.Ready);
        status.ClientUpdateRequired = true;
        var result = LauncherReadinessResolver.Resolve(status);
        Assert.Equal(LauncherReadinessKind.UpdateRequired, result.Kind);
        Assert.Equal("status.launcher.update", result.TextKey);
    }

    [Fact]
    public void RestartRequired_WhenServerNeedsRestart()
    {
        var status = Local(clientReady: true, ServerLifecycleState.RestartRequired);
        var result = LauncherReadinessResolver.Resolve(status);
        Assert.Equal(LauncherReadinessKind.RestartRequired, result.Kind);
        Assert.Equal("status.launcher.restart", result.TextKey);
    }

    [Fact]
    public void Installing_WhenDownloadIsInProgressWithoutClient()
    {
        var status = Local(clientReady: false, ServerLifecycleState.NotInstalled);
        status.Play.Action = PlayActionKind.InstallServer;
        var result = LauncherReadinessResolver.Resolve(status, operationInProgress: true);
        Assert.Equal(LauncherReadinessKind.Installing, result.Kind);
        Assert.Equal("status.launcher.installing", result.TextKey);
        Assert.True(result.IsBusy);
    }

    [Fact]
    public void Updating_WhenDownloadIsInProgressWithClient()
    {
        var status = Local(clientReady: true, ServerLifecycleState.Updating);
        status.ClientUpdateRequired = true;
        var result = LauncherReadinessResolver.Resolve(status, operationInProgress: true);
        Assert.Equal(LauncherReadinessKind.Installing, result.Kind);
        Assert.Equal("status.launcher.updating", result.TextKey);
        Assert.True(result.IsBusy);
    }

    [Theory]
    [InlineData(ServerLifecycleState.StartingDatabase)]
    [InlineData(ServerLifecycleState.StartingAuth)]
    [InlineData(ServerLifecycleState.InitializingWorld)]
    public void Starting_HidesInternalProcessNames(ServerLifecycleState state)
    {
        var status = Local(clientReady: true, state);
        var result = LauncherReadinessResolver.Resolve(status);
        Assert.Equal(LauncherReadinessKind.Starting, result.Kind);
        Assert.Equal("status.launcher.starting", result.TextKey);
        Assert.True(result.IsBusy);
    }

    [Theory]
    [InlineData(ServerLifecycleState.NotInstalled)]
    [InlineData(ServerLifecycleState.Stopped)]
    public void Offline_WhenLocalServerIsDown(ServerLifecycleState state)
    {
        var status = Local(clientReady: true, state);
        var result = LauncherReadinessResolver.Resolve(status);
        Assert.Equal(LauncherReadinessKind.Offline, result.Kind);
        Assert.Equal("status.launcher.offline", result.TextKey);
    }

    [Fact]
    public void Ready_WhenLocalServerAndClientAreUp()
    {
        var status = Local(clientReady: true, ServerLifecycleState.Ready);
        var result = LauncherReadinessResolver.Resolve(status);
        Assert.Equal(LauncherReadinessKind.Ready, result.Kind);
        Assert.Equal("status.launcher.ready", result.TextKey);
        Assert.False(result.IsBusy);
    }

    [Fact]
    public void Ready_ForRemoteRealm_WhenClientExists()
    {
        var status = new HomeStatus
        {
            ClientReady = true,
            ServerState = ServerLifecycleState.NotInstalled,
            Realm = new RealmEntry { Id = "remote", DisplayName = "Remote", Address = "logon.example.org" }
        };
        var result = LauncherReadinessResolver.Resolve(status);
        Assert.Equal(LauncherReadinessKind.Ready, result.Kind);
    }

    [Fact]
    public void Loc_MapsEveryReadinessKind()
    {
        var loc = new Loc();
        foreach (var kind in Enum.GetValues<LauncherReadinessKind>())
        {
            var status = kind switch
            {
                LauncherReadinessKind.NoClient => Local(false, ServerLifecycleState.Stopped),
                LauncherReadinessKind.Error => Local(true, ServerLifecycleState.Error),
                LauncherReadinessKind.UpdateRequired => WithUpdate(),
                LauncherReadinessKind.RestartRequired => Local(true, ServerLifecycleState.RestartRequired),
                LauncherReadinessKind.Installing => Local(true, ServerLifecycleState.Updating),
                LauncherReadinessKind.Starting => Local(true, ServerLifecycleState.StartingAuth),
                LauncherReadinessKind.Offline => Local(true, ServerLifecycleState.Stopped),
                LauncherReadinessKind.Ready => Local(true, ServerLifecycleState.Ready),
                _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null)
            };
            var result = LauncherReadinessResolver.Resolve(status);
            Assert.Equal(kind, result.Kind);
            Assert.NotEqual(result.TextKey, loc[result.TextKey]);
        }
    }

    private static HomeStatus WithUpdate()
    {
        var status = Local(true, ServerLifecycleState.Ready);
        status.ClientUpdateRequired = true;
        return status;
    }

    private static HomeStatus Local(bool clientReady, ServerLifecycleState state) => new()
    {
        ClientReady = clientReady,
        ServerState = state,
        Realm = new RealmEntry { Id = RealmEntry.LocalServerId, DisplayName = "Local Server" }
    };
}
