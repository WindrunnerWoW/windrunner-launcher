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

}
