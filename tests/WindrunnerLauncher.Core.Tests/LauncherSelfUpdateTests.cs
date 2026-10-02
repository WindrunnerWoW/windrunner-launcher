using WindrunnerLauncher.Core.Updates;

namespace WindrunnerLauncher.Core.Tests;

public class LauncherSelfUpdateTests
{
    [Fact]
    public void ResolveExecutablePath_AppImageTargetsOriginalFileInsteadOfMountedBinary()
    {
        using var tmp = new TempDir();
        var appImage = tmp.Combine("downloads", "WindrunnerLauncher.AppImage");
        var mountedBinary = tmp.Combine("mount", "usr", "bin", "WindrunnerLauncher");

        Assert.Equal(appImage, LauncherSelfUpdate.ResolveExecutablePath(appImage, mountedBinary));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("relative.AppImage")]
    public void ResolveExecutablePath_WithoutAbsoluteAppImageUsesProcessPath(string? appImage)
    {
        using var tmp = new TempDir();
        var binary = tmp.Combine("WindrunnerLauncher");

        Assert.Equal(binary, LauncherSelfUpdate.ResolveExecutablePath(appImage, binary));
    }

    [Fact]
    public void Stage_AppImageRetainsPreviousPackageAndMarksUpdatePending()
    {
        using var tmp = new TempDir();
        var target = tmp.File("WindrunnerLauncher.AppImage", "old-package");
        var download = tmp.File("update.AppImage", "new-package");
        var state = new Persistence.StateStore(tmp.Paths());
        var updater = new LauncherSelfUpdate(state);

        var previous = updater.Stage(download, LauncherSelfUpdate.ResolveExecutablePath(target, null));

        Assert.Equal("new-package", File.ReadAllText(target));
        Assert.Equal("old-package", File.ReadAllText(previous));
        Assert.True(updater.PendingVerification);
        Assert.True(updater.RestorePrevious(target));
        Assert.Equal("old-package", File.ReadAllText(target));
    }
}
