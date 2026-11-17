using WindrunnerLauncher.App.ViewModels;
using WindrunnerLauncher.Core;
using Xunit;

namespace WindrunnerLauncher.App.Tests;

public class ServerConfigViewModelTests
{
    /// <summary>A throwaway launcher root, deleted on dispose.</summary>
    private sealed class TempDir : IDisposable
    {
        public TempDir()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"launcher-app-tests-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public LauncherPaths Paths() => new(Path);

        public void Dispose()
        {
            try
            {
                Directory.Delete(Path, recursive: true);
            }
            catch (IOException)
            {
                // A lingering handle is fine; the OS cleans the temp folder up.
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }

    private static string ConfPath(LauncherRuntime runtime, string file) =>
        Path.Combine(runtime.Paths.ServerBinaries, file);

    private static ServerConfigEntryViewModel Entry(ServerConfigViewModel vm, string id) =>
        vm.Groups.SelectMany(g => g.Entries).Single(e => e.Entry.Id == id);

    [Fact]
    public void Constructor_ReadsCurrentValuesFromTheLiveConf()
    {
        using var tmp = new TempDir();
        using var runtime = new LauncherRuntime(tmp.Paths());
        File.WriteAllText(ConfPath(runtime, "mangosd.conf"), "MinPetitionSigns = 9\nRate.XP.Kill = 1.5\n");

        var vm = new ServerConfigViewModel(runtime);

        Assert.Equal(9m, Entry(vm, "gameplay.petition.signs").Number);
        Assert.Equal(1.5m, Entry(vm, "rate.xp.kill").Number);
    }

    [Fact]
    public void Constructor_PrefersAStoredOverrideOverTheFile()
    {
        using var tmp = new TempDir();
        using var runtime = new LauncherRuntime(tmp.Paths());
        File.WriteAllText(ConfPath(runtime, "mangosd.conf"), "MinPetitionSigns = 9\n");
        runtime.State.Settings.ServerTuning["gameplay.petition.signs"] = "5";

        var vm = new ServerConfigViewModel(runtime);

        Assert.Equal(5m, Entry(vm, "gameplay.petition.signs").Number);
    }

    [Fact]
    public void Save_WithoutEdits_StoresNoOverrides()
    {
        using var tmp = new TempDir();
        using var runtime = new LauncherRuntime(tmp.Paths());
        File.WriteAllText(ConfPath(runtime, "mangosd.conf"), "MinPetitionSigns = 9\n");

        var vm = new ServerConfigViewModel(runtime);
        vm.SaveCommand.Execute(null);

        Assert.Empty(runtime.State.Settings.ServerTuning);
    }

    [Fact]
    public void Save_StoresOnlyTheChangedKey_AndWritesItToTheConf()
    {
        using var tmp = new TempDir();
        using var runtime = new LauncherRuntime(tmp.Paths());
        File.WriteAllText(ConfPath(runtime, "mangosd.conf"), "MinPetitionSigns = 9\nRate.XP.Kill = 1\n");

        var vm = new ServerConfigViewModel(runtime);
        Entry(vm, "gameplay.petition.signs").Number = 5m;
        vm.SaveCommand.Execute(null);

        Assert.Equal("5", runtime.State.Settings.ServerTuning["gameplay.petition.signs"]);
        Assert.Single(runtime.State.Settings.ServerTuning);
        Assert.Contains("MinPetitionSigns = 5", File.ReadAllText(ConfPath(runtime, "mangosd.conf")));
    }

    [Fact]
    public void Save_KeepsAnExistingOverride_WhenItIsNotEdited()
    {
        using var tmp = new TempDir();
        using var runtime = new LauncherRuntime(tmp.Paths());
        File.WriteAllText(ConfPath(runtime, "mangosd.conf"), "MinPetitionSigns = 9\n");
        runtime.State.Settings.ServerTuning["gameplay.petition.signs"] = "5";

        var vm = new ServerConfigViewModel(runtime);
        vm.SaveCommand.Execute(null);

        Assert.Equal("5", runtime.State.Settings.ServerTuning["gameplay.petition.signs"]);
    }

    [Fact]
    public void Save_ReplacingAnOverride_StoresTheNewValue()
    {
        using var tmp = new TempDir();
        using var runtime = new LauncherRuntime(tmp.Paths());
        File.WriteAllText(ConfPath(runtime, "mangosd.conf"), "MinPetitionSigns = 9\n");
        runtime.State.Settings.ServerTuning["gameplay.petition.signs"] = "5";

        var vm = new ServerConfigViewModel(runtime);
        Entry(vm, "gameplay.petition.signs").Number = 7m;
        vm.SaveCommand.Execute(null);

        Assert.Equal("7", runtime.State.Settings.ServerTuning["gameplay.petition.signs"]);
        Assert.Contains("MinPetitionSigns = 7", File.ReadAllText(ConfPath(runtime, "mangosd.conf")));
    }

    [Fact]
    public void Save_WritesPortsToTheFriendlySettings()
    {
        using var tmp = new TempDir();
        using var runtime = new LauncherRuntime(tmp.Paths());

        var vm = new ServerConfigViewModel(runtime)
        {
            MysqlPort = 3310,
            AuthPort = 3800,
            WorldPort = 8095
        };
        vm.SaveCommand.Execute(null);

        var saved = runtime.State.Settings.Server;
        Assert.Equal(3310, saved.MysqlPort);
        Assert.Equal(3800, saved.AuthPort);
        Assert.Equal(8095, saved.WorldPort);
    }

    [Fact]
    public void Save_RaisesSaved_AndCancelRaisesCancelled()
    {
        using var tmp = new TempDir();
        using var runtime = new LauncherRuntime(tmp.Paths());
        var vm = new ServerConfigViewModel(runtime);
        var saved = 0;
        var cancelled = 0;
        vm.Saved += () => saved++;
        vm.Cancelled += () => cancelled++;

        vm.SaveCommand.Execute(null);
        vm.CancelCommand.Execute(null);

        Assert.Equal(1, saved);
        Assert.Equal(1, cancelled);
    }

    [Fact]
    public void Reset_DropsOverrides_AndRestoresTheShippedValues()
    {
        using var tmp = new TempDir();
        using var runtime = new LauncherRuntime(tmp.Paths());
        File.WriteAllText(Path.Combine(runtime.Paths.ServerBinaries, "mangosd.conf.dist"), "MinPetitionSigns = 9\n");
        File.WriteAllText(ConfPath(runtime, "mangosd.conf"), "MinPetitionSigns = 9\n");
        runtime.State.Settings.ServerTuning["gameplay.petition.signs"] = "5";

        var vm = new ServerConfigViewModel(runtime);
        Assert.Equal(5m, Entry(vm, "gameplay.petition.signs").Number);

        vm.ResetCommand.Execute(null);

        Assert.Empty(runtime.State.Settings.ServerTuning);
        Assert.Equal(9m, Entry(vm, "gameplay.petition.signs").Number);
        Assert.Contains("MinPetitionSigns = 9", File.ReadAllText(ConfPath(runtime, "mangosd.conf")));
    }
}
