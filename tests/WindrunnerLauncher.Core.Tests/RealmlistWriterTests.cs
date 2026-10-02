using WindrunnerLauncher.Core.Client;

namespace WindrunnerLauncher.Core.Tests;

public class RealmlistWriterTests
{
    [Theory]
    [InlineData("127.0.0.1", 3724, "127.0.0.1")]
    [InlineData("127.0.0.1", 0, "127.0.0.1")]
    [InlineData("logon.example.org", 3724, "logon.example.org")]
    [InlineData("logon.example.org", 3725, "logon.example.org:3725")]
    [InlineData("  logon.example.org  ", 4000, "logon.example.org:4000")]
    [InlineData("host:9999", 3725, "host:9999")]
    [InlineData("", 3724, "127.0.0.1")]
    [InlineData("   ", 3800, "127.0.0.1:3800")]
    public void FormatHost(string address, int port, string expected)
    {
        Assert.Equal(expected, RealmlistWriter.FormatHost(address, port));
    }

    [Fact]
    public void Write_RootRealmlist_IsQuotedSetRealmlistLine()
    {
        using var tmp = new TempDir();
        var client = tmp.Dir("client");
        var written = RealmlistWriter.Write(client, "127.0.0.1");

        var root = Path.Combine(client, "realmlist.wtf");
        Assert.Contains(root, written);
        Assert.Equal("set realmlist \"127.0.0.1\"\n", File.ReadAllText(root));

        var bytes = File.ReadAllBytes(root);
        Assert.False(bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF, "no UTF-8 BOM");
    }

    [Fact]
    public void Write_WithPort_IsQuotedHostPort()
    {
        using var tmp = new TempDir();
        var client = tmp.Dir("client");
        RealmlistWriter.Write(client, RealmlistWriter.FormatHost("logon.example.org", 3725));
        Assert.Equal("set realmlist \"logon.example.org:3725\"\n", File.ReadAllText(Path.Combine(client, "realmlist.wtf")));
    }

    [Fact]
    public void Write_AlsoWritesLocaleRealmlists_AndConfigWtf()
    {
        using var tmp = new TempDir();
        var client = tmp.Dir("client");
        tmp.Dir("client/Data/enUS");
        tmp.Dir("client/Data/deDE");
        tmp.Dir("client/Data/zhCN");
        tmp.Dir("client/Data/Interface");
        tmp.Dir("client/Data/x");

        var written = RealmlistWriter.Write(client, "10.0.0.1", patchHost: "patch.example.org");

        foreach (var locale in new[] { "enUS", "deDE", "zhCN" })
        {
            var file = Path.Combine(client, "Data", locale, "realmlist.wtf");
            Assert.Contains(file, written);
            Assert.Equal("set realmlist \"10.0.0.1\"\n", File.ReadAllText(file));
        }

        Assert.False(File.Exists(Path.Combine(client, "Data", "Interface", "realmlist.wtf")), "non-locale dirs are skipped");
        Assert.False(File.Exists(Path.Combine(client, "Data", "x", "realmlist.wtf")));

        var config = Path.Combine(client, "WTF", "Config.wtf");
        Assert.Contains(config, written);
        var values = ConfigWtf.Read(config);
        Assert.Equal("10.0.0.1", values["realmList"]);
        Assert.Equal("patch.example.org", values["patchList"]);
    }

    [Fact]
    public void Write_PatchListDefaultsToHost()
    {
        using var tmp = new TempDir();
        var client = tmp.Dir("client");
        RealmlistWriter.Write(client, "10.0.0.2");
        var values = ConfigWtf.Read(Path.Combine(client, "WTF", "Config.wtf"));
        Assert.Equal("10.0.0.2", values["patchList"]);
    }

    [Fact]
    public void Write_Overwrites_PreviousRealm()
    {
        using var tmp = new TempDir();
        var client = tmp.Dir("client");
        RealmlistWriter.Write(client, "first.example.org");
        RealmlistWriter.Write(client, "second.example.org");
        var text = File.ReadAllText(Path.Combine(client, "realmlist.wtf"));
        Assert.Equal("set realmlist \"second.example.org\"\n", text);
        Assert.Single(text.Split('\n', StringSplitOptions.RemoveEmptyEntries));
    }

    [Fact]
    public void Write_MissingClientDir_Throws()
    {
        using var tmp = new TempDir();
        Assert.Throws<DirectoryNotFoundException>(() => RealmlistWriter.Write(tmp.Combine("nope"), "x"));
        Assert.Throws<DirectoryNotFoundException>(() => RealmlistWriter.Write("", "x"));
    }

    [Fact]
    public void LocaleDirectories_KnownLocalesFirst_NoDuplicates()
    {
        using var tmp = new TempDir();
        var client = tmp.Dir("client");
        tmp.Dir("client/Data/frFR");
        tmp.Dir("client/Data/enUS");
        tmp.Dir("client/Data/ruRU");
        var dirs = RealmlistWriter.LocaleDirectories(client).Select(Path.GetFileName).ToList();
        Assert.Equal(3, dirs.Count);
        Assert.Equal(3, dirs.Distinct().Count());
        Assert.Equal("enUS", dirs[0]);
        Assert.Equal("frFR", dirs[1]);
        Assert.Equal("ruRU", dirs[2]);
    }

    [Fact]
    public void LocaleDirectories_NoDataFolder_IsEmpty()
    {
        using var tmp = new TempDir();
        Assert.Empty(RealmlistWriter.LocaleDirectories(tmp.Dir("client")));
    }
}

public class ConfigWtfTests
{
    [Fact]
    public void Read_MissingFile_IsEmpty()
    {
        using var tmp = new TempDir();
        Assert.Empty(ConfigWtf.Read(tmp.Combine("WTF", "Config.wtf")));
    }

    [Fact]
    public void SetValues_CreatesFile_WithCrlfAndQuotedValues()
    {
        using var tmp = new TempDir();
        var path = tmp.Combine("WTF", "Config.wtf");
        ConfigWtf.SetValues(path, new Dictionary<string, string> { ["realmList"] = "127.0.0.1" });
        Assert.Equal("SET realmList \"127.0.0.1\"\r\n", File.ReadAllText(path));
    }

    [Fact]
    public void SetValues_PreservesUnknownLines_AndReplacesInPlace()
    {
        using var tmp = new TempDir();
        var path = tmp.File("WTF/Config.wtf",
            "SET locale \"enUS\"\r\n" +
            "SET realmList \"old.example.org\"\r\n" +
            "   SET gxResolution \"1920x1080\"\r\n" +
            "\r\n");

        ConfigWtf.SetValues(path, new Dictionary<string, string> { ["REALMLIST"] = "new.example.org", ["patchList"] = "new.example.org" });

        var lines = File.ReadAllText(path).Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal("SET locale \"enUS\"", lines[0]);
        Assert.Equal("SET realmList \"new.example.org\"", lines[1]);
        Assert.Equal("   SET gxResolution \"1920x1080\"", lines[2]);
        Assert.Equal("SET patchList \"new.example.org\"", lines[3]);
        Assert.Equal(4, lines.Length);

        var read = ConfigWtf.Read(path);
        Assert.Equal("new.example.org", read["realmlist"]);
        Assert.Equal("1920x1080", read["gxResolution"]);
        Assert.Equal("enUS", read["locale"]);
    }

    [Fact]
    public void Remove_DropsOnlyRequestedKeys()
    {
        using var tmp = new TempDir();
        var path = tmp.File("Config.wtf", "SET a \"1\"\r\nSET b \"2\"\r\nSET c \"3\"\r\n");
        ConfigWtf.Remove(path, ["B"]);
        var read = ConfigWtf.Read(path);
        Assert.Equal(2, read.Count);
        Assert.False(read.ContainsKey("b"));
        Assert.Equal("1", read["a"]);
        Assert.Equal("3", read["c"]);
    }

    [Fact]
    public void Remove_MissingFile_IsNoOp()
    {
        using var tmp = new TempDir();
        ConfigWtf.Remove(tmp.Combine("nope.wtf"), ["a"]);
        Assert.False(File.Exists(tmp.Combine("nope.wtf")));
    }
}
