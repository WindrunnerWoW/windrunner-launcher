using WindrunnerLauncher.Core.Security;

namespace WindrunnerLauncher.Core.Server;

/// <summary>
/// Creates the mangos user, loads <c>sql/create_databases.sql</c>, grants on <c>tw_*</c>,
/// imports the base world data, migrations, playerbot and module SQL in
/// <c>sql/BUILD_INFO.txt</c> order, and upserts realmlist id=1. Migrations are recorded
/// the way mangosd's DB auto-updater records them, so it only applies newer ones.
/// </summary>
public sealed class SqlImporter
{
    private readonly LauncherPaths _paths;
    private readonly PortableEnv _env;
    private readonly MariaDbManager _maria;

    public SqlImporter(LauncherPaths paths, PortableEnv env, MariaDbManager maria)
    {
        _paths = paths ?? throw new ArgumentNullException(nameof(paths));
        _env = env ?? throw new ArgumentNullException(nameof(env));
        _maria = maria ?? throw new ArgumentNullException(nameof(maria));
    }

    /// <summary>
    /// Imports databases. When <paramref name="forceReimport"/> is set, or a previous
    /// import left <c>.setup-incomplete</c>, drops <c>tw_*</c> first.
    /// </summary>
    public async Task ImportAsync(bool forceReimport = false, CancellationToken ct = default, Action<string>? log = null)
    {
        _env.Reload();
        Directory.CreateDirectory(_paths.ServerData);
        var complete = _paths.SetupCompleteMarker;
        var incomplete = _paths.SetupIncompleteMarker;
        var recovering = File.Exists(incomplete);

        if (File.Exists(complete) && !forceReimport && !recovering)
        {
            log?.Invoke("already imported (" + complete + ") - pass force to wipe and reload");
            await SyncRealmlistAsync(ct, log).ConfigureAwait(false);
            return;
        }

        ServerUtil.WriteMarker(incomplete, DateTime.UtcNow.ToString("O"));
        if (File.Exists(complete))
            File.Delete(complete);

        if (forceReimport || recovering)
        {
            log?.Invoke("dropping tw_* databases");
            await DropTwDatabasesAsync(ct).ConfigureAwait(false);
        }

        await ImportCoreAsync(ct, log).ConfigureAwait(false);
        ServerUtil.WriteMarker(complete, DateTime.UtcNow.ToString("O"));
        if (File.Exists(incomplete))
            File.Delete(incomplete);
        log?.Invoke("Import done.");
    }

    /// <summary>CREATE USER + GRANT for mangos@localhost and @127.0.0.1, then ALTER password.</summary>
    public async Task EnsureDatabaseUserAsync(CancellationToken ct = default, Action<string>? log = null)
    {
        _env.Reload();
        var user = _env.Get("MYSQL_USER", PortableEnv.DefaultMysqlUser);
        var pass = _env.Get("MYSQL_PASSWORD", PortableEnv.DefaultMysqlPassword);
        var userSql = ServerUtil.SqlLiteral(user);
        var passSql = ServerUtil.SqlLiteral(pass);
        log?.Invoke("syncing database credentials for " + user);
        var sql = $"""
            CREATE USER IF NOT EXISTS {userSql}@'localhost' IDENTIFIED BY {passSql};
            CREATE USER IF NOT EXISTS {userSql}@'127.0.0.1' IDENTIFIED BY {passSql};
            ALTER USER {userSql}@'localhost' IDENTIFIED BY {passSql};
            ALTER USER {userSql}@'127.0.0.1' IDENTIFIED BY {passSql};
            GRANT ALL PRIVILEGES ON tw_char.* TO {userSql}@'localhost';
            GRANT ALL PRIVILEGES ON tw_logon.* TO {userSql}@'localhost';
            GRANT ALL PRIVILEGES ON tw_world.* TO {userSql}@'localhost';
            GRANT ALL PRIVILEGES ON tw_logs.* TO {userSql}@'localhost';
            GRANT ALL PRIVILEGES ON tw_char.* TO {userSql}@'127.0.0.1';
            GRANT ALL PRIVILEGES ON tw_logon.* TO {userSql}@'127.0.0.1';
            GRANT ALL PRIVILEGES ON tw_world.* TO {userSql}@'127.0.0.1';
            GRANT ALL PRIVILEGES ON tw_logs.* TO {userSql}@'127.0.0.1';
            FLUSH PRIVILEGES;
            """;
        await _maria.InvokeSqlAsync(sql, null, false, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Upserts <c>tw_logon.realmlist</c> id=1 with name/address/port, icon=0, realmflags=0,
    /// timezone=1, allowedSecurityLevel=0, population=0, realmbuilds='7272'.
    /// </summary>
    public async Task SyncRealmlistAsync(CancellationToken ct = default, Action<string>? log = null)
    {
        _env.Reload();
        var realmName = _env.Get("REALM_NAME", PortableEnv.DefaultRealmName);
        var realmAddress = _env.Get("REALM_ADDRESS", PortableEnv.DefaultRealmAddress);
        var worldPort = _env.GetInt("WORLD_PORT", PortableEnv.DefaultWorldPort);
        var nameSql = ServerUtil.SqlLiteral(realmName);
        var addrSql = ServerUtil.SqlLiteral(realmAddress);
        var sql = $"""
            INSERT INTO tw_logon.realmlist (id, name, address, port, icon, realmflags, timezone, allowedSecurityLevel, population, realmbuilds)
            VALUES (1, {nameSql}, {addrSql}, {worldPort}, 0, 0, 1, 0, 0, '7272')
            ON DUPLICATE KEY UPDATE name=VALUES(name), address=VALUES(address), port=VALUES(port);
            """;
        log?.Invoke($"syncing realmlist ({realmName} -> {realmAddress}:{worldPort})");
        await _maria.InvokeSqlAsync(sql, null, false, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Drops and rebuilds <c>tw_world</c> only: the <c>tw_world</c> section of
    /// <c>sql/create_databases.sql</c>, every file in <c>sql/base</c>, then the world parts of
    /// migrations, playerbots and modules. Accounts (<c>tw_logon</c>) and characters
    /// (<c>tw_char</c>) are left untouched.
    /// </summary>
    public async Task ReimportWorldAsync(CancellationToken ct = default, Action<string>? log = null)
    {
        _env.Reload();
        var createSql = Path.Combine(_paths.Sql, "create_databases.sql");
        if (!File.Exists(createSql))
            throw new FileNotFoundException("missing sql/create_databases.sql - fetch SQL first", createSql);
        var baseDir = Path.Combine(_paths.Sql, "base");
        var baseFiles = ListSql(baseDir);
        if (baseFiles.Count == 0)
            throw new DirectoryNotFoundException("no SQL files in " + baseDir);

        Directory.CreateDirectory(_paths.ServerData);
        var worldSchema = Path.Combine(_paths.ServerData, "create_world.sql");
        await WriteWorldSchemaAsync(createSql, worldSchema, ct).ConfigureAwait(false);
        try
        {
            log?.Invoke("dropping tw_world");
            await _maria.InvokeSqlAsync("DROP DATABASE IF EXISTS tw_world;", null, false, ct).ConfigureAwait(false);

            log?.Invoke("Importing tw_world schema from create_databases.sql ...");
            await ImportFileAsync(worldSchema, null, false, ct, log).ConfigureAwait(false);
            await EnsureDatabaseUserAsync(ct, log).ConfigureAwait(false);

            log?.Invoke($"Importing {baseFiles.Count} base world files into tw_world ...");
            foreach (var file in baseFiles)
            {
                ct.ThrowIfCancellationRequested();
                await ImportFileAsync(file, "tw_world", false, ct, log).ConfigureAwait(false);
            }

            await ImportExtrasAsync(WorldOnly, ct, log).ConfigureAwait(false);
        }
        finally
        {
            File.Delete(worldSchema);
        }

        log?.Invoke("World database rebuilt.");
    }

    /// <summary>
    /// Copies the dump header plus the <c>tw_world</c> section of <paramref name="createSql"/>.
    /// Running the whole file would also drop and recreate every character and account table.
    /// </summary>
    internal static async Task WriteWorldSchemaAsync(string createSql, string target, CancellationToken ct)
    {
        await using var writer = new StreamWriter(target, append: false, new System.Text.UTF8Encoding(false));
        var seenDatabase = false;
        var inWorld = false;
        var found = false;
        foreach (var line in File.ReadLines(createSql))
        {
            ct.ThrowIfCancellationRequested();
            if (line.StartsWith("CREATE DATABASE", StringComparison.OrdinalIgnoreCase))
            {
                seenDatabase = true;
                inWorld = line.Contains("`tw_world`", StringComparison.OrdinalIgnoreCase);
                found |= inWorld;
            }

            if (!seenDatabase || inWorld)
                await writer.WriteLineAsync(line).ConfigureAwait(false);
        }

        if (!found)
            throw new InvalidDataException("sql/create_databases.sql has no tw_world section");
    }

    public Task DropTwDatabasesAsync(CancellationToken ct = default) =>
        _maria.InvokeSqlAsync(
            "DROP DATABASE IF EXISTS tw_world; DROP DATABASE IF EXISTS tw_char; DROP DATABASE IF EXISTS tw_logon; DROP DATABASE IF EXISTS tw_logs;",
            null, false, ct);

    private async Task ImportCoreAsync(CancellationToken ct, Action<string>? log)
    {
        var user = _env.Get("MYSQL_USER", PortableEnv.DefaultMysqlUser);
        var pass = _env.Get("MYSQL_PASSWORD", PortableEnv.DefaultMysqlPassword);
        var userSql = ServerUtil.SqlLiteral(user);
        var passSql = ServerUtil.SqlLiteral(pass);

        log?.Invoke($"Creating user {user} (no grants yet) ...");
        var createUser = $"CREATE USER IF NOT EXISTS {userSql}@'localhost' IDENTIFIED BY {passSql};\nCREATE USER IF NOT EXISTS {userSql}@'127.0.0.1' IDENTIFIED BY {passSql};\nFLUSH PRIVILEGES;";
        await _maria.InvokeSqlAsync(createUser, null, false, ct).ConfigureAwait(false);

        var createSql = Path.Combine(_paths.Sql, "create_databases.sql");
        if (!File.Exists(createSql))
            throw new FileNotFoundException("missing sql/create_databases.sql - fetch SQL first", createSql);
        log?.Invoke("Importing create_databases.sql ...");
        await ImportFileAsync(createSql, null, false, ct, log).ConfigureAwait(false);

        log?.Invoke("Granting on tw_* ...");
        var grant = $"""
            GRANT ALL PRIVILEGES ON tw_char.* TO {userSql}@'localhost';
            GRANT ALL PRIVILEGES ON tw_logon.* TO {userSql}@'localhost';
            GRANT ALL PRIVILEGES ON tw_world.* TO {userSql}@'localhost';
            GRANT ALL PRIVILEGES ON tw_logs.* TO {userSql}@'localhost';
            GRANT ALL PRIVILEGES ON tw_char.* TO {userSql}@'127.0.0.1';
            GRANT ALL PRIVILEGES ON tw_logon.* TO {userSql}@'127.0.0.1';
            GRANT ALL PRIVILEGES ON tw_world.* TO {userSql}@'127.0.0.1';
            GRANT ALL PRIVILEGES ON tw_logs.* TO {userSql}@'127.0.0.1';
            FLUSH PRIVILEGES;
            """;
        await _maria.InvokeSqlAsync(grant, null, false, ct).ConfigureAwait(false);

        var baseDir = Path.Combine(_paths.Sql, "base");
        if (!Directory.Exists(baseDir))
            throw new DirectoryNotFoundException("missing " + baseDir);
        var baseFiles = ListSql(baseDir);
        log?.Invoke($"Importing {baseFiles.Count} base world files into tw_world ...");
        foreach (var file in baseFiles)
        {
            ct.ThrowIfCancellationRequested();
            await ImportFileAsync(file, "tw_world", false, ct, log).ConfigureAwait(false);
        }

        await ImportExtrasAsync(AllDatabases, ct, log).ConfigureAwait(false);
        await SyncRealmlistAsync(ct, log).ConfigureAwait(false);
    }

    private static readonly string[] AllDatabases = ["tw_logon", "tw_char", "tw_world"];
    private static readonly string[] WorldOnly = ["tw_world"];

    /// <summary>
    /// Imports everything after <c>sql/base</c> in the order <c>sql/BUILD_INFO.txt</c>
    /// prescribes: database_updates, playerbots world then characters, then modules
    /// auth, character, world. Migrations must land first: the playerbot indexes are
    /// built on core loot tables that migrations may still reshape.
    /// </summary>
    private async Task ImportExtrasAsync(string[] databases, CancellationToken ct, Action<string>? log)
    {
        foreach (var (folder, db) in UpdateFolders)
        {
            if (databases.Contains(db))
                await ApplyMigrationsAsync(folder, db, ct, log).ConfigureAwait(false);
        }

        var playerbots = Path.Combine(_paths.Sql, "playerbots");
        if (!Directory.Exists(playerbots))
            throw new DirectoryNotFoundException("missing " + playerbots + " - mangosd needs the playerbot tables");
        if (databases.Contains("tw_world"))
        {
            var world = Path.Combine(playerbots, "world");
            var files = ListSql(world).Concat(ListSql(Path.Combine(world, "classic"))).ToList();
            log?.Invoke($"Importing {files.Count} playerbot world files into tw_world ...");
            await ImportAllAsync(files, "tw_world", ct, log).ConfigureAwait(false);
        }
        if (databases.Contains("tw_char"))
        {
            var files = ListSql(Path.Combine(playerbots, "characters"));
            log?.Invoke($"Importing {files.Count} playerbot character files into tw_char ...");
            await ImportAllAsync(files, "tw_char", ct, log).ConfigureAwait(false);
        }

        var modulesDir = Path.Combine(_paths.Sql, "modules");
        var modules = Directory.Exists(modulesDir) ? Directory.GetDirectories(modulesDir) : [];
        Array.Sort(modules, StringComparer.Ordinal);
        foreach (var (folder, db) in UpdateFolders)
        {
            if (!databases.Contains(db))
                continue;
            foreach (var module in modules)
            {
                var files = ListSql(Path.Combine(module, "data", "sql", folder));
                if (files.Count == 0)
                    continue;
                var name = Path.GetFileName(module);
                log?.Invoke($"Applying {files.Count} {name} {folder} migrations to {db} ...");
                await ApplyTrackedAsync(files, db, name, ct, log).ConfigureAwait(false);
            }
        }
    }

    /// <summary>
    /// Folder names match mangosd's <c>Database.AutoUpdate.*UpdateName</c>; auth, character,
    /// world is also the module order from <c>BUILD_INFO.txt</c>.
    /// </summary>
    private static readonly (string Folder, string Database)[] UpdateFolders =
    [
        ("auth", "tw_logon"),
        ("character", "tw_char"),
        ("world", "tw_world"),
    ];

    /// <summary>
    /// Applies <c>sql/database_updates/&lt;folder&gt;</c> in name order and records each file the
    /// way mangosd's auto-updater does (file stem + uppercase SHA1 of the bytes), so mangosd
    /// skips them on start. Loose files directly in <c>database_updates</c> are ignored, as
    /// mangosd ignores them. No <c>--force</c>: a failing migration must stop setup.
    /// </summary>
    private async Task ApplyMigrationsAsync(string folder, string database, CancellationToken ct, Action<string>? log)
    {
        var files = ListSql(Path.Combine(_paths.Sql, "database_updates", folder));
        if (files.Count == 0)
            return;

        log?.Invoke($"Applying {files.Count} {folder} migrations to {database} ...");
        await ApplyTrackedAsync(files, database, "", ct, log).ConfigureAwait(false);
    }

    /// <summary>
    /// Imports each file and records it in <paramref name="database"/>'s <c>migrations</c> table.
    /// mangosd keys applied migrations by module + hash, so <paramref name="module"/> is "" for
    /// core updates and the module folder name for module SQL (what mangosd reads from
    /// <c>Database.AutoUpdate.ModulesPath</c>). Servers without that setting never read module
    /// rows; the ones with it skip what was applied here and only run newer files.
    /// </summary>
    private async Task ApplyTrackedAsync(List<string> files, string database, string module, CancellationToken ct, Action<string>? log)
    {
        await _maria.InvokeSqlAsync(MigrationsTableSql, database, false, ct).ConfigureAwait(false);
        var moduleSql = ServerUtil.SqlLiteral(module);
        foreach (var file in files)
        {
            ct.ThrowIfCancellationRequested();
            await ImportFileAsync(file, database, false, ct, log).ConfigureAwait(false);
            var name = ServerUtil.SqlLiteral(Path.GetFileNameWithoutExtension(file));
            var hash = ServerUtil.SqlLiteral(Checksums.Sha1HexUpper(await File.ReadAllBytesAsync(file, ct).ConfigureAwait(false)));
            await _maria.InvokeSqlAsync(
                $"INSERT INTO `migrations` (`Name`, `Module`, `Hash`, `AppliedAt`) VALUES ({name}, {moduleSql}, {hash}, NOW());",
                database, false, ct).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// mangosd's own DDL. The table from <c>create_databases.sql</c> predates the <c>Module</c>
    /// column, which mangosd adds on first start; add it here so module rows can be written.
    /// </summary>
    private const string MigrationsTableSql = """
        CREATE TABLE IF NOT EXISTS `migrations` (
            `Id` INT(10) UNSIGNED NOT NULL AUTO_INCREMENT,
            `Name` VARCHAR(255) NOT NULL DEFAULT '0' COLLATE 'utf8_general_ci',
            `Module` VARCHAR(255) NOT NULL DEFAULT '' COLLATE 'utf8_general_ci',
            `Hash` VARCHAR(128) NOT NULL DEFAULT '0' COLLATE 'utf8_general_ci',
            `AppliedAt` DATETIME NOT NULL,
            PRIMARY KEY(`Id`) USING BTREE
        ) COLLATE = 'utf8_general_ci' ENGINE = InnoDB;
        ALTER TABLE `migrations` ADD COLUMN IF NOT EXISTS `Module` VARCHAR(255) NOT NULL DEFAULT '' COLLATE 'utf8_general_ci' AFTER `Name`;
        """;

    private async Task ImportAllAsync(List<string> files, string database, CancellationToken ct, Action<string>? log)
    {
        foreach (var file in files)
        {
            ct.ThrowIfCancellationRequested();
            await ImportFileAsync(file, database, false, ct, log).ConfigureAwait(false);
        }
    }

    private async Task ImportFileAsync(string file, string? database, bool force, CancellationToken ct, Action<string>? log)
    {
        log?.Invoke(" " + Path.GetFileName(file));
        await _maria.InvokeSqlFileAsync(file, database, force, ct).ConfigureAwait(false);
    }

    private static List<string> ListSql(string dir)
    {
        if (!Directory.Exists(dir))
            return [];
        var files = Directory.GetFiles(dir, "*.sql", SearchOption.TopDirectoryOnly);
        Array.Sort(files, StringComparer.Ordinal);
        return [.. files];
    }
}
