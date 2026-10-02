using WindrunnerLauncher.Core.Client;
using WindrunnerLauncher.Core.Downloads;
using WindrunnerLauncher.Core.Models;
using WindrunnerLauncher.Core.Mods;
using WindrunnerLauncher.Core.Persistence;

namespace WindrunnerLauncher.Core.Tests;

public class DllsTxtTests
{
    [Fact]
    public void Read_MissingFile_IsEmpty()
    {
        using var tmp = new TempDir();
        Assert.Empty(DllsTxt.Read(tmp.Path));
        Assert.False(DllsTxt.HasEntries(tmp.Path));
    }

    [Fact]
    public void Write_ProducesDllsTxt_AndCacheWithAbsoluteWindowsPaths()
    {
        using var tmp = new TempDir();
        DllsTxt.Write(tmp.Path, ["nampower.dll", "SuperWoW.dll"]);

        var txt = File.ReadAllText(DllsTxt.PathFor(tmp.Path));
        Assert.Equal("nampower.dll\nSuperWoW.dll\n", txt);

        var cache = File.ReadAllText(DllsTxt.CachePathFor(tmp.Path));
        var expectedRoot = Path.GetFullPath(tmp.Path).TrimEnd('/', '\\').Replace('/', '\\');
        Assert.Equal($"{expectedRoot}\\nampower.dll\r\n{expectedRoot}\\SuperWoW.dll", cache);
    }

    [Fact]
    public void Write_FiltersStockDllsCommentsBlanksAndDuplicates()
    {
        using var tmp = new TempDir();
        DllsTxt.Write(tmp.Path, ["  nampower.dll ", "", "# comment", "fmod.dll", "twloader.dll", "NAMPOWER.dll", "dbghelp"]);
        Assert.Equal(new[] { "nampower.dll" }, DllsTxt.Read(tmp.Path));
    }

    [Fact]
    public void Write_EmptyList_DeletesBothFiles()
    {
        using var tmp = new TempDir();
        DllsTxt.Write(tmp.Path, ["a.dll"]);
        Assert.True(File.Exists(DllsTxt.PathFor(tmp.Path)));
        Assert.True(File.Exists(DllsTxt.CachePathFor(tmp.Path)));

        DllsTxt.Write(tmp.Path, ["fmod.dll"]);
        Assert.False(File.Exists(DllsTxt.PathFor(tmp.Path)));
        Assert.False(File.Exists(DllsTxt.CachePathFor(tmp.Path)));
    }

    [Fact]
    public void Add_Remove_Contains()
    {
        using var tmp = new TempDir();
        Assert.True(DllsTxt.Add(tmp.Path, "a.dll"));
        Assert.False(DllsTxt.Add(tmp.Path, "A.DLL"), "duplicates are case-insensitive");
        Assert.False(DllsTxt.Add(tmp.Path, "fmod.dll"), "stock dlls are never injected");
        Assert.False(DllsTxt.Add(tmp.Path, "  "));
        Assert.True(DllsTxt.Add(tmp.Path, "b.dll"));
        Assert.Equal(new[] { "a.dll", "b.dll" }, DllsTxt.Read(tmp.Path));
        Assert.True(DllsTxt.Contains(tmp.Path, "B.dll"));

        Assert.True(DllsTxt.Remove(tmp.Path, "A.dll"));
        Assert.False(DllsTxt.Remove(tmp.Path, "A.dll"));
        Assert.Equal(new[] { "b.dll" }, DllsTxt.Read(tmp.Path));

        Assert.True(DllsTxt.Remove(tmp.Path, "b.dll"));
        Assert.False(File.Exists(DllsTxt.PathFor(tmp.Path)));
    }

    [Fact]
    public void Read_IgnoresCommentsAndBlankLines()
    {
        using var tmp = new TempDir();
        tmp.File(DllsTxt.FileName, "# injected by launcher\n\n  a.dll  \r\nb.dll\na.dll\n");
        Assert.Equal(new[] { "a.dll", "b.dll" }, DllsTxt.Read(tmp.Path));
    }

    [Theory]
    [InlineData("fmod.dll", true)]
    [InlineData("FMOD.DLL", true)]
    [InlineData("twloader.dll", true)]
    [InlineData("unicows", true)]
    [InlineData("nampower.dll", false)]
    [InlineData("VanillaFixes.dll", false)]
    public void IsStock(string name, bool expected) => Assert.Equal(expected, DllsTxt.IsStock(name));
}

public class WdbTests
{
    [Fact]
    public void ClearWdb_RemovesBothCacheLocations()
    {
        using var tmp = new TempDir();
        tmp.File("client/WDB/creaturecache.wdb", "x");
        tmp.File("client/Cache/WDB/enUS/itemcache.wdb", "x");
        tmp.File("client/Cache/other.bin", "keep");
        var client = tmp.Combine("client");

        Assert.True(Wdb.HasCache(client));
        var removed = Wdb.ClearWdb(client);

        Assert.Equal(2, removed.Count);
        Assert.False(Directory.Exists(tmp.Combine("client", "WDB")));
        Assert.False(Directory.Exists(tmp.Combine("client", "Cache", "WDB")));
        Assert.True(File.Exists(tmp.Combine("client", "Cache", "other.bin")));
        Assert.False(Wdb.HasCache(client));
    }

    [Fact]
    public void ClearWdb_NothingToDo_ReturnsEmpty()
    {
        using var tmp = new TempDir();
        Assert.Empty(Wdb.ClearWdb(tmp.Dir("client")));
        Assert.Empty(Wdb.ClearWdb(tmp.Combine("missing")));
        Assert.Empty(Wdb.ClearWdb(""));
        Assert.False(Wdb.HasCache(""));
    }
}

public class ClientManagerTests
{
    private static (TempDir Tmp, LauncherPaths Paths, StateStore State, ClientManager Client) Create()
    {
        var tmp = new TempDir();
        var paths = tmp.Paths();
        paths.EnsureLayout();
        var state = new StateStore(paths);
        return (tmp, paths, state, new ClientManager(paths, state));
    }

    [Fact]
    public async Task BootstrapAsync_RequiresChecksumBeforeDownloading()
    {
        var (tmp, _, _, client) = Create();
        using var _ = tmp;
        using var downloads = new DownloadManager();
        var manifest = new ClientManifest
        {
            Bootstrap = new ClientBootstrap { Url = "https://example.invalid/client.zip" }
        };

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            client.BootstrapAsync(manifest, downloads));
    }

    [Fact]
    public void IsValid_RequiresAWowExecutable()
    {
        var (tmp, paths, _, client) = Create();
        using var _ = tmp;
        Assert.False(client.IsValid(null));
        Assert.False(client.IsValid(""));
        Assert.False(client.IsValid(tmp.Combine("missing")));
        Assert.False(client.IsValid(paths.Client));

        File.WriteAllText(Path.Combine(paths.Client, "WoW.exe"), "MZ");
        Assert.True(client.IsValid(paths.Client));
    }

    [Fact]
    public void FindExecutable_IsCaseInsensitive()
    {
        using var tmp = new TempDir();
        var dir = tmp.Dir("c");
        File.WriteAllText(Path.Combine(dir, "wow.EXE"), "MZ");
        Assert.NotNull(ClientManager.FindExecutable(dir));
        Assert.Null(ClientManager.FindExecutable(tmp.Dir("empty")));
    }

    [Theory]
    [InlineData("patch.mpq", true)]
    [InlineData("patch-1.mpq", true)]
    [InlineData("Data/patch-2.MPQ", true)]
    [InlineData("patch-5.mpq", true)]
    [InlineData("patch-6.mpq", true)]
    [InlineData("patch-9.mpq", true)]
    [InlineData("base.mpq", true)]
    [InlineData("patch-A.mpq", false)]
    [InlineData("patch-H.mpq", false)]
    [InlineData("patch-10.mpq", false)]
    public void IsOfficialMpq(string name, bool expected) => Assert.Equal(expected, ClientManager.IsOfficialMpq(name));

    [Fact]
    public void ResolveClientPath_DefaultsToManagedClient_OrConfiguredPath()
    {
        var (tmp, paths, state, client) = Create();
        using var _ = tmp;
        Assert.Equal(paths.Client, client.ResolveClientPath());
        var custom = tmp.Dir("custom");
        state.Settings.ClientPath = custom;
        Assert.Equal(Path.GetFullPath(custom), client.ResolveClientPath());
        Assert.Equal(Path.GetFullPath(custom), client.ClientForRealm(new RealmEntry()));
        var overridden = tmp.Dir("override");
        Assert.Equal(Path.GetFullPath(overridden), client.ClientForRealm(new RealmEntry { ClientDirectoryOverride = overridden }));
    }

    [Fact]
    public async Task ImportAsync_ManagedCopy_CopiesTreeAndCreatesCleanBackup()
    {
        var (tmp, paths, state, client) = Create();
        using var _ = tmp;
        var source = tmp.Dir("source");
        File.WriteAllText(Path.Combine(source, "WoW.exe"), "MZ-original");
        tmp.File("source/Data/base.mpq", "base");
        tmp.File("source/WTF/Config.wtf", "SET locale \"enUS\"");

        await client.ImportAsync(source, createManagedCopy: true);

        Assert.Equal("MZ-original", File.ReadAllText(Path.Combine(paths.Client, "WoW.exe")));
        Assert.Equal("base", File.ReadAllText(Path.Combine(paths.Client, "Data", "base.mpq")));
        Assert.True(File.Exists(Path.Combine(paths.Client, "WTF", "Config.wtf")));
        Assert.True(File.Exists(Path.Combine(source, "WoW.exe")), "source is copied, not moved");

        var backup = Path.Combine(paths.Client, ClientManager.CleanBackupFileName);
        Assert.True(File.Exists(backup));
        Assert.Equal("MZ-original", File.ReadAllText(backup));

        Assert.Equal(paths.Client, state.Settings.ClientPath);
        Assert.True(state.Settings.ClientIsManagedCopy);
        Assert.Equal(backup, state.Settings.CleanWowExeBackupPath);
        Assert.Equal(paths.Client, new StateStore(paths).Settings.ClientPath);
    }

    [Fact]
    public async Task ImportAsync_InPlace_DoesNotCopy()
    {
        var (tmp, paths, state, client) = Create();
        using var _ = tmp;
        var source = tmp.Dir("source");
        File.WriteAllText(Path.Combine(source, "WoW.exe"), "MZ");

        await client.ImportAsync(source, createManagedCopy: false);

        Assert.Equal(Path.GetFullPath(source), state.Settings.ClientPath);
        Assert.False(state.Settings.ClientIsManagedCopy);
        Assert.False(File.Exists(Path.Combine(paths.Client, "WoW.exe")));
        Assert.True(File.Exists(Path.Combine(source, ClientManager.CleanBackupFileName)));
    }

    [Fact]
    public async Task ImportAsync_InvalidSource_Throws()
    {
        var (tmp, _, _, client) = Create();
        using var _ = tmp;
        await Assert.ThrowsAsync<DirectoryNotFoundException>(() => client.ImportAsync(tmp.Combine("nope"), true));
        await Assert.ThrowsAsync<InvalidOperationException>(() => client.ImportAsync(tmp.Dir("noexe"), true));
    }

    [Fact]
    public void EnsureCleanExecutableBackup_DoesNotOverwriteExistingBackup()
    {
        var (tmp, paths, _, client) = Create();
        using var _ = tmp;
        File.WriteAllText(Path.Combine(paths.Client, "WoW.exe"), "patched");
        File.WriteAllText(Path.Combine(paths.Client, ClientManager.CleanBackupFileName), "pristine");

        var backup = client.EnsureCleanExecutableBackup(paths.Client);

        Assert.NotNull(backup);
        Assert.Equal("pristine", File.ReadAllText(backup!));
        Assert.Equal(backup, client.CleanExecutableBackupFor(paths.Client));
        Assert.Null(client.EnsureCleanExecutableBackup(tmp.Dir("noexe")));
    }

    [Fact]
    public void CleanExecutableBackupFor_DoesNotUseAnotherClientsBackup()
    {
        var (tmp, _, state, client) = Create();
        using var _ = tmp;
        var first = tmp.Dir("client-a");
        var second = tmp.Dir("client-b");
        File.WriteAllText(Path.Combine(first, ClientManager.CleanBackupFileName), "a");
        File.WriteAllText(Path.Combine(second, "WoW.exe"), "b");
        state.Settings.CleanWowExeBackupPath = Path.Combine(first, ClientManager.CleanBackupFileName);

        Assert.Null(client.CleanExecutableBackupFor(second));
    }

    private sealed class RecordingRepairer : IManagedAssetRepairer
    {
        public int Required;
        public int Restored;
        public bool? LastStrict;

        public Task ApplyRequiredAsync(RealmEntry realm, CancellationToken ct = default)
        {
            Required++;
            return Task.CompletedTask;
        }

        public Task RestoreManagedStateAsync(RealmEntry realm, bool strict, CancellationToken ct = default)
        {
            Restored++;
            LastStrict = strict;
            return Task.CompletedTask;
        }
    }

    [Fact]
    public async Task RepairAsync_RecreatesScaffolding_KeepsUnknownFiles()
    {
        var (tmp, paths, state, client) = Create();
        using var _ = tmp;
        File.WriteAllText(Path.Combine(paths.Client, "WoW.exe"), "MZ");
        File.WriteAllText(Path.Combine(paths.Client, "user-notes.txt"), "mine");
        var repairer = new RecordingRepairer();

        var report = await client.RepairAsync(repairer, state.SelectedRealm());

        Assert.False(report.Deep);
        Assert.Equal(1, repairer.Required);
        Assert.Equal(0, repairer.Restored);
        foreach (var dir in new[] { "Data", "Interface", Path.Combine("Interface", "AddOns"), "WTF", "Logs" })
            Assert.True(Directory.Exists(Path.Combine(paths.Client, dir)), dir);
        Assert.True(File.Exists(Path.Combine(paths.Client, "user-notes.txt")));
        Assert.Contains(report.Actions, a => a.Contains("Recreated", StringComparison.Ordinal));
        Assert.Empty(report.Warnings);
    }

    [Fact]
    public async Task DeepRepairAsync_RestoresManagedStateStrictly()
    {
        var (tmp, paths, state, client) = Create();
        using var _ = tmp;
        File.WriteAllText(Path.Combine(paths.Client, "WoW.exe"), "MZ");
        var repairer = new RecordingRepairer();

        var report = await client.DeepRepairAsync(repairer, state.SelectedRealm());

        Assert.True(report.Deep);
        Assert.Equal(1, repairer.Required);
        Assert.Equal(1, repairer.Restored);
        Assert.True(repairer.LastStrict);
    }

    [Fact]
    public async Task RepairAsync_NoClient_WarnsAndDoesNothing()
    {
        var (tmp, _, state, client) = Create();
        using var _ = tmp;
        var repairer = new RecordingRepairer();
        var report = await client.RepairAsync(repairer, state.SelectedRealm());
        Assert.Single(report.Warnings);
        Assert.Equal(0, repairer.Required);
    }
}
