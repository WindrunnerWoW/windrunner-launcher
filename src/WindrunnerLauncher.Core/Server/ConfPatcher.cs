using System.Text.RegularExpressions;

namespace WindrunnerLauncher.Core.Server;

/// <summary>
/// Copies <c>*.conf.dist</c> (or <c>.dist.in</c> / <c>conf/</c>) over the live server
/// configs and patches database, path, port, and playerbot keys.
/// </summary>
public sealed class ConfPatcher
{
    private static readonly (string DistName, string LiveName, bool Patch)[] ConfFiles =
    [
        ("mangosd.conf.dist", "mangosd.conf", true),
        ("realmd.conf.dist", "realmd.conf", true),
        ("aiplayerbot.conf.dist", "aiplayerbot.conf", true),
        ("ahbot.conf.dist", "ahbot.conf", false)
    ];

    private static readonly (string Pattern, string Key, string Database)[] DatabaseKeys =
    [
        (@"LoginDatabase\.Info\s*=\s*"".*""", "LoginDatabase.Info", "tw_logon"),
        (@"LoginDatabaseInfo\s*=\s*"".*""", "LoginDatabaseInfo", "tw_logon"),
        (@"WorldDatabase\.Info\s*=\s*"".*""", "WorldDatabase.Info", "tw_world"),
        (@"CharacterDatabase\.Info\s*=\s*"".*""", "CharacterDatabase.Info", "tw_char"),
        (@"LogsDatabase\.Info\s*=\s*"".*""", "LogsDatabase.Info", "tw_logs")
    ];

    private readonly LauncherPaths _paths;
    private readonly PortableEnv _env;

    public ConfPatcher(LauncherPaths paths, PortableEnv env)
    {
        _paths = paths ?? throw new ArgumentNullException(nameof(paths));
        _env = env ?? throw new ArgumentNullException(nameof(env));
    }

    /// <summary>
    /// Overwrites live confs from dist when one exists (otherwise keeps the shipped live conf),
    /// then patches database lines and known keys. <c>ahbot.conf</c> is copied only.
    /// </summary>
    public void Apply()
    {
        _env.Reload();
        Directory.CreateDirectory(_paths.ServerBinaries);
        Directory.CreateDirectory(_paths.Logs);
        Directory.CreateDirectory(_paths.Maps);

        foreach (var (distName, liveName, patch) in ConfFiles)
        {
            var dist = ResolveDist(distName);
            var live = Path.Combine(_paths.ServerBinaries, liveName);
            if (dist is not null)
                File.Copy(dist, live, overwrite: true);
            else if (!File.Exists(live))
                continue;
            // Release zips may ship ready-made live confs with no dist; those are patched in place.
            if (patch)
                PatchLive(liveName, live);
        }
    }

    private void PatchLive(string liveName, string livePath)
    {
        var port = _env.GetInt("MYSQL_PORT", PortableEnv.DefaultMysqlPort);
        var user = _env.Get("MYSQL_USER", PortableEnv.DefaultMysqlUser);
        var password = _env.Get("MYSQL_PASSWORD", PortableEnv.DefaultMysqlPassword);
        PatchDatabaseLines(livePath, "127.0.0.1", port, user, password);

        if (liveName.Equals("mangosd.conf", StringComparison.OrdinalIgnoreCase))
        {
            SetValue(livePath, "DataDir", Quoted(ServerUtil.IniPath(_paths.Maps)));
            SetValue(livePath, "LogsDir", Quoted(ServerUtil.IniPath(_paths.Logs)));
            SetValue(livePath, "WorldServerPort", _env.Get("WORLD_PORT", PortableEnv.DefaultWorldPort.ToString()));
            SetValue(livePath, "Database.AutoUpdate.Enabled", "1");
            var updates = ServerUtil.IniPath(Path.Combine(_paths.Sql, "database_updates"));
            SetValue(livePath, "Database.AutoUpdate.Path", Quoted(updates.TrimEnd('/') + "/"));
            var modules = ServerUtil.IniPath(Path.Combine(_paths.Sql, "modules"));
            SetValue(livePath, "Database.AutoUpdate.ModulesPath", Quoted(modules.TrimEnd('/') + "/"));
            SetValue(livePath, "LogSQL", "0");
            SetValue(livePath, "LFT.BotFill.Enable", "1");
            SetValue(livePath, "SoloDungeonRepopAlive.Enable", "1");
            SetValue(livePath, "Leech.Enable", "1");
        }
        else if (liveName.Equals("realmd.conf", StringComparison.OrdinalIgnoreCase))
        {
            SetValue(livePath, "LogsDir", Quoted(ServerUtil.IniPath(_paths.Logs).TrimEnd('/') + "/"));
            SetValue(livePath, "RealmServerPort", _env.Get("REALM_PORT", PortableEnv.DefaultRealmPort.ToString()));
        }
        else if (liveName.Equals("aiplayerbot.conf", StringComparison.OrdinalIgnoreCase))
        {
            SetValue(livePath, "AiPlayerbot.Enabled", "1");
            SetValue(livePath, "AiPlayerbot.RandomBotAutoCreate", "1");
            SetValue(livePath, "AiPlayerbot.DeleteRandomBotAccounts", "0");
            SetValue(livePath, "AiPlayerbot.MinRandomBots", _env.Get("MIN_RANDOM_BOTS", PortableEnv.DefaultMinRandomBots.ToString()));
            SetValue(livePath, "AiPlayerbot.MaxRandomBots", _env.Get("MAX_RANDOM_BOTS", PortableEnv.DefaultMaxRandomBots.ToString()));
        }
    }

    private string? ResolveDist(string distName)
    {
        var candidates = new[]
        {
            Path.Combine(_paths.ServerBinaries, distName),
            Path.Combine(_paths.ServerBinaries, distName + ".in"),
            Path.Combine(_paths.Conf, distName)
        };
        return candidates.FirstOrDefault(File.Exists);
    }

    /// <summary>
    /// Rewrites MaNGOS <c>*.Info = "host;port;user;password;dbname"</c> lines.
    /// </summary>
    public static void PatchDatabaseLines(string path, string host, int port, string user, string password)
    {
        if (!File.Exists(path))
            throw new FileNotFoundException("Missing conf.", path);
        foreach (var part in new[] { user, password })
        {
            if (part.IndexOfAny([';', '"', '\r', '\n']) >= 0)
                throw new InvalidOperationException("MYSQL_USER/MYSQL_PASSWORD cannot contain semicolons, double quotes, or line breaks.");
        }

        var info = $"\"{host};{port};{user};{password}";
        var text = File.ReadAllText(path);
        foreach (var (pattern, key, database) in DatabaseKeys)
        {
            var replacement = $"{key} = {info};{database}\"";
            text = Regex.Replace(text, pattern, replacement);
        }

        File.WriteAllText(path, text);
    }

    /// <summary>Sets <paramref name="key"/> = <paramref name="value"/>, keeping existing indent.</summary>
    public static void SetValue(string path, string key, string value)
    {
        if (!File.Exists(path))
            throw new FileNotFoundException("Missing conf.", path);
        var text = File.ReadAllText(path);
        var pattern = new Regex(@"^(\s*)" + Regex.Escape(key) + @"\s*=\s*.*$", RegexOptions.Multiline);
        if (pattern.IsMatch(text))
            text = pattern.Replace(text, "${1}" + key + " = " + value, 1);
        else
            text = text.TrimEnd('\r', '\n') + "\r\n" + key + " = " + value + "\r\n";
        File.WriteAllText(path, text);
    }

    private static string Quoted(string path) => "\"" + path + "\"";
}
