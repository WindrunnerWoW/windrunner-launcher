using WindrunnerLauncher.Core.Server;

namespace WindrunnerLauncher.Core.Tests;

public class ServerConfigCatalogTests
{
    private static string Lines(params string[] lines) => string.Join("\n", lines) + "\n";

    private static readonly string RateConf = Lines(
        "# Progressive rates",
        "",
        "[Default]",
        "Rate.XP.Kill    = 1",
        "",
        "[LowLevel]",
        "Rate.XP.Kill    = 0.9",
        "",
        "[Settings]",
        "1_8 = LowLevel");

    [Fact]
    public void Catalog_IdsAreUnique_AndEveryGroupIsDeclared()
    {
        var ids = ServerConfigCatalog.Entries.Select(e => e.Id).ToList();
        Assert.Equal(ids.Count, ids.Distinct(StringComparer.Ordinal).Count());

        var groups = ServerConfigCatalog.Groups.Select(g => g.Id).ToHashSet(StringComparer.Ordinal);
        Assert.All(ServerConfigCatalog.Entries, e => Assert.Contains(e.Group, groups));
    }

    [Fact]
    public void Catalog_EveryEntryIsReachableByFind()
    {
        Assert.All(ServerConfigCatalog.Entries, e => Assert.Same(e, ServerConfigCatalog.Find(e.Id)));
        Assert.Null(ServerConfigCatalog.Find("nope"));
    }

    [Fact]
    public void Read_FlatFile_ReturnsValue()
    {
        using var tmp = new TempDir();
        var path = tmp.File("mangosd.conf", Lines("MaxPlayerLevel = 60", "Rate.XP.Kill = 1.5"));

        Assert.Equal("60", ServerConfigFile.Read(path, "", "MaxPlayerLevel"));
        Assert.Equal("1.5", ServerConfigFile.Read(path, "", "Rate.XP.Kill"));
        Assert.Null(ServerConfigFile.Read(path, "", "Missing"));
    }

    [Fact]
    public void Read_MissingFile_ReturnsNull()
    {
        using var tmp = new TempDir();
        Assert.Null(ServerConfigFile.Read(tmp.Combine("nope.conf"), "", "Key"));
    }

    [Fact]
    public void Read_Section_ScopesToThatSection()
    {
        using var tmp = new TempDir();
        var path = tmp.File("rate.conf", RateConf);

        Assert.Equal("1", ServerConfigFile.Read(path, "Default", "Rate.XP.Kill"));
        Assert.Equal("0.9", ServerConfigFile.Read(path, "LowLevel", "Rate.XP.Kill"));
        Assert.Null(ServerConfigFile.Read(path, "Missing", "Rate.XP.Kill"));
    }

    [Fact]
    public void Write_FlatFile_KeepsIndentAndOtherLines()
    {
        using var tmp = new TempDir();
        var path = tmp.File("mangosd.conf", Lines("A = 1", "  MaxPlayerLevel = 60", "B = 2"));

        ServerConfigFile.Write(path, "", "MaxPlayerLevel", "70");

        Assert.Equal(Lines("A = 1", "  MaxPlayerLevel = 70", "B = 2"), File.ReadAllText(path));
    }

    [Fact]
    public void Write_FlatFile_AppendsMissingKey()
    {
        using var tmp = new TempDir();
        var path = tmp.File("mangosd.conf", Lines("A = 1"));

        ServerConfigFile.Write(path, "", "Rate.Honor", "2");

        Assert.Equal(Lines("A = 1", "Rate.Honor = 2"), File.ReadAllText(path));
    }

    [Fact]
    public void Write_Section_OnlyTouchesThatSection()
    {
        using var tmp = new TempDir();
        var path = tmp.File("rate.conf", RateConf);

        ServerConfigFile.Write(path, "LowLevel", "Rate.XP.Kill", "0.75");

        var text = File.ReadAllText(path);
        Assert.Contains(Lines("[Default]", "Rate.XP.Kill    = 1"), text);
        Assert.Contains(Lines("[LowLevel]", "Rate.XP.Kill = 0.75"), text);
        Assert.Contains(Lines("[Settings]", "1_8 = LowLevel"), text);
    }

    [Fact]
    public void Write_Section_AppendsMissingKeyInsideSection()
    {
        using var tmp = new TempDir();
        var path = tmp.File("rate.conf", RateConf);

        ServerConfigFile.Write(path, "Default", "Rate.XP.Quest", "2");

        Assert.Contains(
            Lines("[Default]", "Rate.XP.Kill    = 1", "Rate.XP.Quest = 2", "", "[LowLevel]"),
            File.ReadAllText(path));
    }

    [Fact]
    public void Write_MissingSection_AppendsSection()
    {
        using var tmp = new TempDir();
        var path = tmp.File("rate.conf", Lines("[Default]", "Rate.XP.Kill = 1"));

        ServerConfigFile.Write(path, "Hardcore", "Rate.XP.Kill", "0.5");

        var text = File.ReadAllText(path);
        Assert.Contains(Lines("[Default]", "Rate.XP.Kill = 1"), text);
        Assert.Contains(Lines("[Hardcore]", "Rate.XP.Kill = 0.5"), text);
        Assert.Equal("0.5", ServerConfigFile.Read(path, "Hardcore", "Rate.XP.Kill"));
    }

    [Fact]
    public void Write_PreservesCrlfLineEndings()
    {
        using var tmp = new TempDir();
        var path = tmp.File("mangosd.conf", "A = 1\r\nMaxPlayerLevel = 60\r\n");

        ServerConfigFile.Write(path, "", "MaxPlayerLevel", "70");

        Assert.Equal("A = 1\r\nMaxPlayerLevel = 70\r\n", File.ReadAllText(path));
    }

    [Fact]
    public void Write_MissingFile_Throws()
    {
        using var tmp = new TempDir();
        Assert.Throws<FileNotFoundException>(() => ServerConfigFile.Write(tmp.Combine("nope.conf"), "", "k", "v"));
    }

    [Fact]
    public void ApplyTuning_WritesOverridesAfterDistCopy()
    {
        using var tmp = new TempDir();
        var paths = tmp.Paths();
        paths.EnsureLayout();
        File.WriteAllText(Path.Combine(paths.ServerBinaries, "mangosd.conf.dist"),
            Lines("MinPetitionSigns = 9", "Rate.XP.Kill = 1"));
        File.WriteAllText(Path.Combine(paths.ServerBinaries, "rate.conf"), RateConf);

        var tuning = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["gameplay.petition.signs"] = "5",
            ["rate.xp.kill"] = "2",
            ["progression.lowlevel"] = "0.75"
        };
        new ConfPatcher(paths, new PortableEnv(paths)).Apply(tuning);

        var mangosd = File.ReadAllText(Path.Combine(paths.ServerBinaries, "mangosd.conf"));
        Assert.Contains("MinPetitionSigns = 5", mangosd);
        Assert.Contains("Rate.XP.Kill = 2", mangosd);

        var rates = File.ReadAllText(Path.Combine(paths.ServerBinaries, "rate.conf"));
        Assert.Contains(Lines("[LowLevel]", "Rate.XP.Kill = 0.75"), rates);
        Assert.Contains(Lines("[Default]", "Rate.XP.Kill    = 1"), rates);
    }

    [Fact]
    public void ApplyTuning_WithoutOverrides_LeavesShippedValues()
    {
        using var tmp = new TempDir();
        var paths = tmp.Paths();
        paths.EnsureLayout();
        File.WriteAllText(Path.Combine(paths.ServerBinaries, "mangosd.conf.dist"), Lines("MinPetitionSigns = 9"));

        new ConfPatcher(paths, new PortableEnv(paths)).Apply();

        Assert.Contains("MinPetitionSigns = 9",
            File.ReadAllText(Path.Combine(paths.ServerBinaries, "mangosd.conf")));
    }

    [Fact]
    public void ApplyTuning_SkipsFilesThatAreNotInstalled()
    {
        using var tmp = new TempDir();
        var paths = tmp.Paths();
        paths.EnsureLayout();

        var tuning = new Dictionary<string, string>(StringComparer.Ordinal) { ["gameplay.petition.signs"] = "5" };
        new ConfPatcher(paths, new PortableEnv(paths)).Apply(tuning);

        Assert.False(File.Exists(Path.Combine(paths.ServerBinaries, "mangosd.conf")));
    }
}
