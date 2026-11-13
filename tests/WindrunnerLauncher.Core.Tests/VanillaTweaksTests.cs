using WindrunnerLauncher.Core.Client;
using WindrunnerLauncher.Core.Models;
using WindrunnerLauncher.Core.Mods;

namespace WindrunnerLauncher.Core.Tests;

public sealed class VanillaTweaksTests
{
    [Fact]
    public void SettingsFingerprint_ChangesWhenEffectiveSettingsChange()
    {
        var settings = new VanillaTweaksSettings { UseRecommendedPreset = false };
        using var tmp = new TempDir();
        var clean = tmp.Combine("clean.exe");
        var live = tmp.Combine("live.exe");
        File.WriteAllText(clean, "clean");
        File.WriteAllText(live, "live");
        var original = VanillaTweaks.SettingsFingerprint(settings, clean, live);
        settings.FarClip++;

        Assert.NotEqual(original, VanillaTweaks.SettingsFingerprint(settings, clean, live));
    }

    [Fact]
    public void SettingsFingerprint_UsesEffectiveRecommendedPreset()
    {
        var recommended = VanillaTweaks.RecommendedPreset();
        var custom = new VanillaTweaksSettings
        {
            UseRecommendedPreset = true,
            FarClip = 1,
            FieldOfViewRadians = 0
        };

        using var tmp = new TempDir();
        var clean = tmp.Combine("clean.exe");
        var live = tmp.Combine("live.exe");
        File.WriteAllText(clean, "clean");
        File.WriteAllText(live, "live");
        Assert.Equal(VanillaTweaks.SettingsFingerprint(recommended, clean, live), VanillaTweaks.SettingsFingerprint(custom, clean, live));
    }

    [Fact]
    public void IsCurrent_RejectsChangedSettingsAndLiveExecutable()
    {
        using var tmp = new TempDir();
        var client = tmp.Dir("client");
        var clean = Path.Combine(client, "WoW-OriginalBackup.exe");
        var live = Path.Combine(client, "WoW.exe");
        File.WriteAllText(clean, "clean");
        File.WriteAllText(live, "patched");
        var settings = new VanillaTweaksSettings { UseRecommendedPreset = false };

        VanillaTweaks.RecordAppliedState(client, clean, settings);
        Assert.True(VanillaTweaks.IsCurrent(client, clean, settings));
        settings.FarClip++;
        Assert.False(VanillaTweaks.IsCurrent(client, clean, settings));
        settings.FarClip--;
        File.WriteAllText(live, "externally changed");
        Assert.False(VanillaTweaks.IsCurrent(client, clean, settings));
    }

    [Fact]
    public void EnsureCleanBackup_CreatesOnce_AndNeverOverwrites()
    {
        using var tmp = new TempDir();
        var client = tmp.Dir("client");
        var live = Path.Combine(client, "WoW.exe");
        File.WriteAllText(live, "original");

        var backup = VanillaTweaks.EnsureCleanBackup(client);
        Assert.NotNull(backup);
        Assert.Equal("original", File.ReadAllText(backup!));

        File.WriteAllText(live, "patched");
        Assert.Equal(backup, VanillaTweaks.EnsureCleanBackup(client));
        Assert.Equal("original", File.ReadAllText(backup!));
    }

    [Fact]
    public void RemoveLegacyOutputs_DeletesPatchVariantAndKeepsManagedPair()
    {
        using var tmp = new TempDir();
        var client = tmp.Dir("client");
        var clean = WriteExecutable(Path.Combine(client, ClientManager.CleanBackupFileName), 8192);
        var live = WriteExecutable(Path.Combine(client, "WoW.exe"), 8192);
        var legacy = WriteExecutable(Path.Combine(client, VanillaTweaks.LegacyOutputFileName), 8192);
        PatchByte(live, 3);
        PatchByte(legacy, 7);

        var removed = VanillaTweaks.RemoveLegacyOutputs(client, clean);

        Assert.Equal([legacy], removed);
        Assert.False(File.Exists(legacy));
        Assert.True(File.Exists(live));
        Assert.True(File.Exists(clean));
    }

    [Fact]
    public void RemoveLegacyOutputs_KeepsExecutableThatIsNotAPatchVariant()
    {
        using var tmp = new TempDir();
        var client = tmp.Dir("client");
        var clean = WriteExecutable(Path.Combine(client, ClientManager.CleanBackupFileName), 8192);
        var legacy = Path.Combine(client, VanillaTweaks.LegacyOutputFileName);
        File.WriteAllBytes(legacy, Enumerable.Repeat((byte)0xFF, 8192).ToArray());

        Assert.Empty(VanillaTweaks.RemoveLegacyOutputs(client, clean));
        Assert.True(File.Exists(legacy));
    }

    [Fact]
    public void RemoveLegacyOutputs_RequiresCleanBackup()
    {
        using var tmp = new TempDir();
        var client = tmp.Dir("client");
        var legacy = WriteExecutable(Path.Combine(client, VanillaTweaks.LegacyOutputFileName), 1024);

        Assert.Empty(VanillaTweaks.RemoveLegacyOutputs(client, Path.Combine(client, "missing.exe")));
        Assert.True(File.Exists(legacy));
    }

    private static string WriteExecutable(string path, int length)
    {
        File.WriteAllBytes(path, new byte[length]);
        return path;
    }

    private static void PatchByte(string path, int offset)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.ReadWrite);
        stream.Position = offset;
        stream.WriteByte(0x90);
    }

    [Fact]
    public void BuildArguments_RecommendedPreset_UsesUpstreamFlagNames()
    {
        var args = VanillaTweaks.BuildArguments(new VanillaTweaksSettings(), "in.exe", "out.exe");

        Assert.Equal(new[]
        {
            "-o", "out.exe",
            "--fov", "1.925",
            "--maxcameradistance", "100",
            "--farclip", "777",
            "--nameplatedistance", "41",
            "--no-sound-in-background",
            "--soundchannels", "64",
            "in.exe"
        }, args);
    }

    [Fact]
    public void BuildArguments_EverythingDisabled_NegatesDefaultPatches()
    {
        var settings = new VanillaTweaksSettings
        {
            UseRecommendedPreset = false,
            LargeAddressAware = false,
            FieldOfViewRadians = 0,
            CameraDistanceMax = 0,
            FarClip = 0,
            AlwaysAutoLoot = false,
            SoundInBackground = false,
            NameplateRangeTbc = false,
            MoreSoundChannels = false
        };

        var args = VanillaTweaks.BuildArguments(settings, "in.exe", "out.exe");

        Assert.Equal(new[]
        {
            "-o", "out.exe",
            "--no-largeaddressaware",
            "--no-fov",
            "--no-farclip",
            "--no-nameplatedistance",
            "--no-quickloot",
            "--no-sound-in-background",
            "--no-soundchannels",
            "in.exe"
        }, args);
    }
}
