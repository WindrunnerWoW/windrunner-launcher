using WindrunnerLauncher.Core.Client;
using WindrunnerLauncher.Core.Mods;

namespace WindrunnerLauncher.Core.Tests;

public sealed class ClientBackupIsolationTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task MaterializeDisabledTweaks_NeverRestoresAnotherClientsExecutable(bool hasLocalBackup)
    {
        using var tmp = new TempDir();
        using var runtime = new LauncherRuntime(tmp.Paths());
        var first = tmp.Dir("first");
        var second = tmp.Dir("second");
        File.WriteAllText(Path.Combine(first, "WoW.exe"), "first-client");
        File.WriteAllText(Path.Combine(second, "WoW.exe"), "second-client");
        if (hasLocalBackup)
            runtime.Client.EnsureCleanExecutableBackup(second);
        runtime.Client.EnsureCleanExecutableBackup(first);
        runtime.Mods.Manifest.Assets.Clear();
        runtime.Mods.Manifest.Assets.Add(new ManagedAsset
        {
            Id = "tweaks",
            Kind = ModKind.ExecutablePatch,
            Destination = "WoW.exe",
            DefaultEnabled = false
        });
        var realm = new RealmEntry { Id = "second", ClientDirectoryOverride = second };

        await runtime.Mods.MaterializeAsync(realm);

        Assert.Equal("second-client", File.ReadAllText(Path.Combine(second, "WoW.exe")));
        if (hasLocalBackup)
            Assert.Equal("second-client", File.ReadAllText(Path.Combine(second, ClientManager.CleanBackupFileName)));
    }
}
