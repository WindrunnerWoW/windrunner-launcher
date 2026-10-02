using WindrunnerLauncher.Core.Server;

namespace WindrunnerLauncher.Core.Tests;

public class PortableEnvTests
{
    [Fact]
    public void NoFiles_UsesBuiltInDefaults()
    {
        using var tmp = new TempDir();
        var env = new PortableEnv(tmp.Paths());
        Assert.Equal("3307", env.Get("MYSQL_PORT"));
        Assert.Equal(3307, env.GetInt("MYSQL_PORT", 1));
        Assert.Equal("mangos", env.Get("MYSQL_USER"));
        Assert.Equal("mangos", env.Get("MYSQL_PASSWORD"));
        Assert.Equal("Windrunner", env.Get("REALM_NAME"));
        Assert.Equal("127.0.0.1", env.Get("REALM_ADDRESS"));
        Assert.Equal(3724, env.GetInt("REALM_PORT", 0));
        Assert.Equal(8090, env.GetInt("WORLD_PORT", 0));
        Assert.Equal(20, env.GetInt("MIN_RANDOM_BOTS", 0));
        Assert.Equal(20, env.GetInt("MAX_RANDOM_BOTS", 0));
        Assert.Equal("WindrunnerWoW/windrunner-wow", env.Get("TORTOISE_WOW_REPO"));
        Assert.Equal("latest", env.Get("TORTOISE_WOW_RELEASE"));
    }

    [Fact]
    public void EmbeddedPortableEnv_AgreesWithBuiltInDefaults()
    {
        using var tmp = new TempDir();
        var paths = tmp.Paths();
        var defaults = new PortableEnv(paths);
        EmbeddedResources.ExtractTo("portable.env", paths.PortableEnv);
        var fromFile = new PortableEnv(paths);
        foreach (var key in new[] { "MARIADB_VERSION", "MYSQL_PORT", "MYSQL_USER", "MYSQL_PASSWORD", "REALM_NAME", "REALM_PORT", "WORLD_PORT", "MIN_RANDOM_BOTS", "TORTOISE_WOW_REPO" })
            Assert.Equal(defaults.Get(key), fromFile.Get(key));
    }

    [Fact]
    public void Get_MissingKey_ReturnsDefault_BlankValueReturnsDefault()
    {
        using var tmp = new TempDir();
        var paths = tmp.Paths();
        tmp.File("server/portable.env", "EMPTY=\nSPACES=   \n");
        var env = new PortableEnv(paths);
        Assert.Equal("fallback", env.Get("NOT_THERE", "fallback"));
        Assert.Equal("", env.Get("NOT_THERE"));
        Assert.Equal("d", env.Get("EMPTY", "d"));
        Assert.Equal("d", env.Get("SPACES", "d"));
        Assert.Equal("", env.Get("MYSQL_ROOT_PASSWORD"));
    }

    [Fact]
    public void Get_ThrowsOnBlankName()
    {
        using var tmp = new TempDir();
        var env = new PortableEnv(tmp.Paths());
        Assert.ThrowsAny<ArgumentException>(() => env.Get(""));
        Assert.ThrowsAny<ArgumentException>(() => env.Get("   "));
    }

    [Fact]
    public void LocalEnv_OverridesBaseEnv_WhichOverridesDefaults()
    {
        using var tmp = new TempDir();
        var paths = tmp.Paths();
        tmp.File("server/portable.env", "MYSQL_PORT=3400\nREALM_NAME=Base\n");
        tmp.File("server/portable.local.env", "REALM_NAME=Local\n");
        var env = new PortableEnv(paths);
        Assert.Equal(3400, env.GetInt("MYSQL_PORT", 0));
        Assert.Equal("Local", env.Get("REALM_NAME"));
        Assert.Equal(8090, env.GetInt("WORLD_PORT", 0));
    }

    [Fact]
    public void Parse_SkipsCommentsAndBlankLines_SplitsOnFirstEquals_Trims()
    {
        using var tmp = new TempDir();
        var paths = tmp.Paths();
        tmp.File("server/portable.env",
            "# comment\n" +
            "\n" +
            "   \n" +
            "  KEY_A = value a  \n" +
            "URL=https://example.org/x?y=1&z=2\n" +
            "=novalue\n" +
            "NOEQUALS\n" +
            "KEY_B=b\r\n");
        var env = new PortableEnv(paths);
        Assert.Equal("value a", env.Get("KEY_A"));
        Assert.Equal("https://example.org/x?y=1&z=2", env.Get("URL"));
        Assert.Equal("b", env.Get("KEY_B"));
        Assert.Equal("", env.Get("NOEQUALS"));
        Assert.Equal("", env.Get("# comment"));
    }

    [Fact]
    public void GetInt_NonNumeric_Throws()
    {
        using var tmp = new TempDir();
        var paths = tmp.Paths();
        tmp.File("server/portable.local.env", "MYSQL_PORT=abc\n");
        var env = new PortableEnv(paths);
        var ex = Assert.Throws<InvalidOperationException>(() => env.GetInt("MYSQL_PORT", 1));
        Assert.Contains("MYSQL_PORT", ex.Message);
    }

    [Fact]
    public void Reload_PicksUpChanges()
    {
        using var tmp = new TempDir();
        var paths = tmp.Paths();
        var env = new PortableEnv(paths);
        Assert.Equal("Windrunner", env.Get("REALM_NAME"));
        tmp.File("server/portable.local.env", "REALM_NAME=Changed\n");
        Assert.Equal("Windrunner", env.Get("REALM_NAME"));
        env.Reload();
        Assert.Equal("Changed", env.Get("REALM_NAME"));
    }

    [Fact]
    public void WriteLocal_CreatesFileWithHeader_AndReloads()
    {
        using var tmp = new TempDir();
        var paths = tmp.Paths();
        var env = new PortableEnv(paths);
        env.WriteLocal(new Dictionary<string, string> { ["REALM_NAME"] = "Mine", ["MYSQL_PORT"] = "3333" });

        var text = File.ReadAllText(paths.PortableLocalEnv);
        Assert.StartsWith("#", text);
        Assert.Contains("MYSQL_PORT=3333\n", text);
        Assert.Contains("REALM_NAME=Mine\n", text);
        Assert.Equal("Mine", env.Get("REALM_NAME"));
        Assert.Equal(3333, env.GetInt("MYSQL_PORT", 0));
        Assert.False(File.Exists(paths.PortableEnv), "portable.env is never written by WriteLocal");
    }

    [Fact]
    public void WriteLocal_PreservesCommentsAndUnrelatedKeys_ReplacesInPlace_RemovesEmpty()
    {
        using var tmp = new TempDir();
        var paths = tmp.Paths();
        tmp.File("server/portable.local.env",
            "# my overrides\n" +
            "REALM_NAME=Old\n" +
            "CUSTOM_THING=keep me\n" +
            "TO_REMOVE=bye\n" +
            "junk line without equals\n");
        var env = new PortableEnv(paths);

        env.WriteLocal(new Dictionary<string, string>
        {
            ["REALM_NAME"] = "New",
            ["TO_REMOVE"] = "",
            ["WORLD_PORT"] = "8099"
        });

        var lines = File.ReadAllText(paths.PortableLocalEnv).Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal("# my overrides", lines[0]);
        Assert.Equal("REALM_NAME=New", lines[1]);
        Assert.Equal("CUSTOM_THING=keep me", lines[2]);
        Assert.Equal("junk line without equals", lines[3]);
        Assert.Equal("WORLD_PORT=8099", lines[4]);
        Assert.Equal(5, lines.Length);
        Assert.DoesNotContain(lines, l => l.StartsWith("TO_REMOVE", StringComparison.Ordinal));

        Assert.Equal("New", env.Get("REALM_NAME"));
        Assert.Equal(8099, env.GetInt("WORLD_PORT", 0));
        Assert.Equal("", env.Get("TO_REMOVE"));
    }

    [Fact]
    public void WriteFriendlySettings_MapsAllFields()
    {
        using var tmp = new TempDir();
        var paths = tmp.Paths();
        var env = new PortableEnv(paths);
        env.WriteFriendlySettings(new ServerFriendlySettings
        {
            RealmName = "Friendly",
            RealmAddress = "192.168.0.10",
            AuthPort = 3725,
            WorldPort = 8091,
            MysqlPort = 3308,
            MinRandomBots = 2,
            MaxRandomBots = 40
        });

        Assert.Equal("Friendly", env.Get("REALM_NAME"));
        Assert.Equal("192.168.0.10", env.Get("REALM_ADDRESS"));
        Assert.Equal(3725, env.GetInt("REALM_PORT", 0));
        Assert.Equal(8091, env.GetInt("WORLD_PORT", 0));
        Assert.Equal(3308, env.GetInt("MYSQL_PORT", 0));
        Assert.Equal(2, env.GetInt("MIN_RANDOM_BOTS", 0));
        Assert.Equal(40, env.GetInt("MAX_RANDOM_BOTS", 0));

        var again = new PortableEnv(paths);
        Assert.Equal("Friendly", again.Get("REALM_NAME"));
    }

}
