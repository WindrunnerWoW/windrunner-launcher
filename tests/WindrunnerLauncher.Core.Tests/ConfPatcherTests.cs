using WindrunnerLauncher.Core.Server;

namespace WindrunnerLauncher.Core.Tests;

public class ConfPatcherTests
{
    private const string MangosdDist = """
        ###################################
        # MaNGOS configuration
        ###################################
        [MangosdConf]
        ConfVersion=2010062001

        LoginDatabase.Info = "127.0.0.1;3306;mangos;mangos;realmd"
        WorldDatabase.Info     = "127.0.0.1;3306;mangos;mangos;mangos"
        CharacterDatabase.Info = "127.0.0.1;3306;mangos;mangos;characters"
        LogsDatabase.Info = "127.0.0.1;3306;mangos;mangos;logs"

        DataDir = "."
        LogsDir = ""
        WorldServerPort = 8085
        LogSQL = 1
        """;

    private const string RealmdDist = """
        [RealmdConf]
        ConfVersion=2007062001
        LoginDatabaseInfo = "127.0.0.1;3306;mangos;mangos;realmd"
        LogsDir = ""
        RealmServerPort = 3724
        """;

    [Fact]
    public void PatchDatabaseLines_RewritesEveryInfoLine_WithPortableDatabases()
    {
        using var tmp = new TempDir();
        var path = tmp.File("mangosd.conf", MangosdDist);

        ConfPatcher.PatchDatabaseLines(path, "127.0.0.1", 3307, "mangos", "mangos");

        var text = File.ReadAllText(path);
        Assert.Contains("LoginDatabase.Info = \"127.0.0.1;3307;mangos;mangos;tw_logon\"", text);
        Assert.Contains("WorldDatabase.Info = \"127.0.0.1;3307;mangos;mangos;tw_world\"", text);
        Assert.Contains("CharacterDatabase.Info = \"127.0.0.1;3307;mangos;mangos;tw_char\"", text);
        Assert.Contains("LogsDatabase.Info = \"127.0.0.1;3307;mangos;mangos;tw_logs\"", text);
        Assert.DoesNotContain(";3306;", text);
        Assert.Contains("ConfVersion=2010062001", text);
        Assert.Contains("DataDir = \".\"", text);
    }

    [Fact]
    public void PatchDatabaseLines_RealmdVariant()
    {
        using var tmp = new TempDir();
        var path = tmp.File("realmd.conf", RealmdDist);
        ConfPatcher.PatchDatabaseLines(path, "127.0.0.1", 3399, "user1", "pw1");
        Assert.Contains("LoginDatabaseInfo = \"127.0.0.1;3399;user1;pw1;tw_logon\"", File.ReadAllText(path));
    }

    [Theory]
    [InlineData("bad;user", "pw")]
    [InlineData("user", "pw\"quoted")]
    [InlineData("user", "line\nbreak")]
    [InlineData("us\rer", "pw")]
    public void PatchDatabaseLines_RejectsCredentialsThatBreakTheLineFormat(string user, string pw)
    {
        using var tmp = new TempDir();
        var path = tmp.File("mangosd.conf", MangosdDist);
        Assert.Throws<InvalidOperationException>(() => ConfPatcher.PatchDatabaseLines(path, "127.0.0.1", 3307, user, pw));
        Assert.Equal(MangosdDist, File.ReadAllText(path));
    }

    [Fact]
    public void PatchDatabaseLines_MissingFile_Throws()
    {
        using var tmp = new TempDir();
        Assert.Throws<FileNotFoundException>(() => ConfPatcher.PatchDatabaseLines(tmp.Combine("nope.conf"), "h", 1, "u", "p"));
    }

    [Fact]
    public void SetValue_ReplacesExistingKey_KeepingIndent()
    {
        using var tmp = new TempDir();
        var path = tmp.File("x.conf", "A = 1\n  WorldServerPort = 8085\nB = 2\n");
        ConfPatcher.SetValue(path, "WorldServerPort", "8090");
        Assert.Equal("A = 1\n  WorldServerPort = 8090\nB = 2\n", File.ReadAllText(path));
    }

    [Fact]
    public void SetValue_OnlyReplacesFirstMatch_AndExactKey()
    {
        using var tmp = new TempDir();
        var path = tmp.File("x.conf", "LogsDir = \"\"\nLogsDir.Extra = 5\nLogsDir = \"second\"\n");
        ConfPatcher.SetValue(path, "LogsDir", "\"/srv/logs/\"");
        var text = File.ReadAllText(path);
        Assert.Contains("LogsDir = \"/srv/logs/\"\n", text);
        Assert.Contains("LogsDir.Extra = 5", text);
        Assert.Contains("LogsDir = \"second\"", text);
    }

    [Fact]
    public void SetValue_AppendsMissingKey()
    {
        using var tmp = new TempDir();
        var path = tmp.File("x.conf", "A = 1\r\n");
        ConfPatcher.SetValue(path, "Leech.Enable", "1");
        Assert.Equal("A = 1\r\nLeech.Enable = 1\r\n", File.ReadAllText(path));
    }

    [Fact]
    public void SetValue_ToleratesSpacingVariants()
    {
        using var tmp = new TempDir();
        var path = tmp.File("x.conf", "LogSQL=1\n");
        ConfPatcher.SetValue(path, "LogSQL", "0");
        Assert.Equal("LogSQL = 0\n", File.ReadAllText(path));
    }

    [Fact]
    public void SetValue_MissingFile_Throws()
    {
        using var tmp = new TempDir();
        Assert.Throws<FileNotFoundException>(() => ConfPatcher.SetValue(tmp.Combine("nope.conf"), "k", "v"));
    }

    [Fact]
    public void Apply_CopiesDistOverLive_AndPatchesKnownKeys()
    {
        using var tmp = new TempDir();
        var paths = tmp.Paths();
        paths.EnsureLayout();
        File.WriteAllText(Path.Combine(paths.ServerBinaries, "mangosd.conf.dist"), MangosdDist);
        File.WriteAllText(Path.Combine(paths.ServerBinaries, "realmd.conf.dist"), RealmdDist);
        File.WriteAllText(Path.Combine(paths.ServerBinaries, "aiplayerbot.conf.dist"), "AiPlayerbot.Enabled = 0\nAiPlayerbot.MinRandomBots = 1\n");
        File.WriteAllText(Path.Combine(paths.ServerBinaries, "ahbot.conf.dist"), "AuctionHouseBot.Seller.Enabled = 0\n");
        File.WriteAllText(Path.Combine(paths.ServerBinaries, "mangosd.conf"), "STALE = 1\n");
        File.WriteAllText(paths.PortableLocalEnv, "MYSQL_PORT=3311\nWORLD_PORT=8095\nREALM_PORT=3730\nMIN_RANDOM_BOTS=3\nMAX_RANDOM_BOTS=9\n");

        var env = new PortableEnv(paths);
        new ConfPatcher(paths, env).Apply();

        var mangosd = File.ReadAllText(Path.Combine(paths.ServerBinaries, "mangosd.conf"));
        Assert.DoesNotContain("STALE", mangosd);
        Assert.Contains("WorldDatabase.Info = \"127.0.0.1;3311;mangos;mangos;tw_world\"", mangosd);
        Assert.Contains("WorldServerPort = 8095", mangosd);
        Assert.Contains("LogSQL = 0", mangosd);
        Assert.Contains("Database.AutoUpdate.Enabled = 1", mangosd);
        Assert.Contains("LFT.BotFill.Enable = 1", mangosd);
        Assert.Contains("DataDir = \"" + paths.Maps.Replace('\\', '/') + "\"", mangosd);
        Assert.Contains("LogsDir = \"" + paths.Logs.Replace('\\', '/') + "\"", mangosd);
        Assert.Contains("Database.AutoUpdate.Path = \"" + Path.Combine(paths.Sql, "database_updates").Replace('\\', '/') + "/\"", mangosd);
        Assert.Contains("Database.AutoUpdate.ModulesPath = \"" + Path.Combine(paths.Sql, "modules").Replace('\\', '/') + "/\"", mangosd);
        Assert.DoesNotContain("\\", mangosd.Split('\n').First(l => l.StartsWith("DataDir", StringComparison.Ordinal)));

        var realmd = File.ReadAllText(Path.Combine(paths.ServerBinaries, "realmd.conf"));
        Assert.Contains("LoginDatabaseInfo = \"127.0.0.1;3311;mangos;mangos;tw_logon\"", realmd);
        Assert.Contains("RealmServerPort = 3730", realmd);
        Assert.Contains("LogsDir = \"" + paths.Logs.Replace('\\', '/') + "/\"", realmd);

        var bots = File.ReadAllText(Path.Combine(paths.ServerBinaries, "aiplayerbot.conf"));
        Assert.Contains("AiPlayerbot.Enabled = 1", bots);
        Assert.Contains("AiPlayerbot.MinRandomBots = 3", bots);
        Assert.Contains("AiPlayerbot.MaxRandomBots = 9", bots);
        Assert.Contains("AiPlayerbot.RandomBotAutoCreate = 1", bots);
        Assert.Contains("AiPlayerbot.DeleteRandomBotAccounts = 0", bots);

        Assert.Equal("AuctionHouseBot.Seller.Enabled = 0\n", File.ReadAllText(Path.Combine(paths.ServerBinaries, "ahbot.conf")));
    }

    [Fact]
    public void Apply_FallsBackToDistIn_AndConfDirectory()
    {
        using var tmp = new TempDir();
        var paths = tmp.Paths();
        paths.EnsureLayout();
        File.WriteAllText(Path.Combine(paths.ServerBinaries, "mangosd.conf.dist.in"), MangosdDist);
        File.WriteAllText(Path.Combine(paths.Conf, "realmd.conf.dist"), RealmdDist);

        new ConfPatcher(paths, new PortableEnv(paths)).Apply();

        Assert.True(File.Exists(Path.Combine(paths.ServerBinaries, "mangosd.conf")));
        Assert.True(File.Exists(Path.Combine(paths.ServerBinaries, "realmd.conf")));
        Assert.False(File.Exists(Path.Combine(paths.ServerBinaries, "aiplayerbot.conf")), "missing dist files are skipped");
    }

    [Fact]
    public void Apply_PatchesShippedLiveConfInPlace_WhenNoDist()
    {
        using var tmp = new TempDir();
        var paths = tmp.Paths();
        paths.EnsureLayout();
        File.WriteAllText(Path.Combine(paths.ServerBinaries, "mangosd.conf"), MangosdDist.Replace("DataDir = \".\"", "DataDir = \"../data\"") + "\nCustomKey = 42\n");
        File.WriteAllText(Path.Combine(paths.ServerBinaries, "realmd.conf"), RealmdDist);
        File.WriteAllText(paths.PortableLocalEnv, "MYSQL_PORT=3311\n");

        new ConfPatcher(paths, new PortableEnv(paths)).Apply();

        var mangosd = File.ReadAllText(Path.Combine(paths.ServerBinaries, "mangosd.conf"));
        Assert.Contains("WorldDatabase.Info = \"127.0.0.1;3311;mangos;mangos;tw_world\"", mangosd);
        Assert.Contains("DataDir = \"" + paths.Maps.Replace('\\', '/') + "\"", mangosd);
        Assert.Contains("CustomKey = 42", mangosd);
        var realmd = File.ReadAllText(Path.Combine(paths.ServerBinaries, "realmd.conf"));
        Assert.Contains("LoginDatabaseInfo = \"127.0.0.1;3311;mangos;mangos;tw_logon\"", realmd);
    }

    [Fact]
    public void Apply_IsIdempotent()
    {
        using var tmp = new TempDir();
        var paths = tmp.Paths();
        paths.EnsureLayout();
        File.WriteAllText(Path.Combine(paths.ServerBinaries, "mangosd.conf.dist"), MangosdDist);
        var patcher = new ConfPatcher(paths, new PortableEnv(paths));
        patcher.Apply();
        var first = File.ReadAllText(Path.Combine(paths.ServerBinaries, "mangosd.conf"));
        patcher.Apply();
        Assert.Equal(first, File.ReadAllText(Path.Combine(paths.ServerBinaries, "mangosd.conf")));
    }
}
