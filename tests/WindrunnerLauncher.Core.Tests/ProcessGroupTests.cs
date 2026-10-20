using System.Diagnostics;
using WindrunnerLauncher.Core.Platform;

namespace WindrunnerLauncher.Core.Tests;

public class ProcessGroupTests
{
    [Fact]
    public async Task TracksChildrenAfterTheStartedProcessExits()
    {
        if (!OperatingSystem.IsWindows())
            return;

        // The root shell starts a longer-lived child and exits, mirroring VanillaFixes handing off
        // to WoW.exe. The child inherits the job, so the group must stay alive after the root ends.
        var psi = new ProcessStartInfo
        {
            FileName = "cmd.exe",
            UseShellExecute = false,
            CreateNoWindow = true
        };
        psi.ArgumentList.Add("/c");
        psi.ArgumentList.Add("ping -n 2 127.0.0.1 >nul & start /b ping -n 5 127.0.0.1 >nul");

        using var root = Process.Start(psi);
        Assert.NotNull(root);
        using var group = ProcessGroup.Attach(root);
        Assert.NotNull(group);

        await root.WaitForExitAsync();
        Assert.True(group!.AnyAlive(), "the child process must keep the group alive");

        await group.WaitForExitAsync(TimeSpan.FromSeconds(20));
        Assert.False(group.AnyAlive());
    }
}
