using WindrunnerLauncher.Core.Server;

namespace WindrunnerLauncher.Core.Tests;

public sealed class ServerManagerReadinessTests
{
    [Fact]
    public async Task ReadinessSession_IgnoresRetainedOutputUntilCurrentProcessEmitsSignal()
    {
        var retainedLog = new ConsoleCapture();
        retainedLog.Append("World initialized\n");
        var readiness = new ServerManager.ReadinessSession();

        // The retained diagnostic log is deliberately not connected to this session.
        Assert.False(readiness.Task.IsCompleted);

        readiness.Observe("Loading world data...");
        Assert.False(readiness.Task.IsCompleted);

        readiness.Observe("World initialized");
        Assert.True(await readiness.Task);
    }

    [Fact]
    public async Task ReadinessSession_AcceptsWorldServerUpAndRunningLine()
    {
        var readiness = new ServerManager.ReadinessSession();

        // Startup queries like "SQL: SELECT 1 from creature_movement ..." must not count as ready.
        readiness.Observe("2026-09-25 19:20:43 [299 ms] SQL: SELECT 1 from creature_movement As T LIMIT 1");
        Assert.False(readiness.Task.IsCompleted);

        readiness.Observe("2026-09-25 19:28:02 World server is up and running! Loading time: 1 minutes 31 seconds");
        Assert.True(await readiness.Task);
    }

    [Fact]
    public async Task ReadinessSession_ProcessExitCompletesAsNotReady()
    {
        var readiness = new ServerManager.ReadinessSession();

        readiness.ProcessExited();

        Assert.False(await readiness.Task);
    }
}
