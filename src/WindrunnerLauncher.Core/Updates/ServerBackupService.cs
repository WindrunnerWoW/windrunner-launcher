using WindrunnerLauncher.Core.Server;

namespace WindrunnerLauncher.Core.Updates;

public enum ServerBackupKind
{
    /// <summary>Server files, SQL, configuration, and every realm database.</summary>
    Full,

    /// <summary>Accounts and characters only (<c>tw_logon</c>, <c>tw_char</c>).</summary>
    Characters
}

public sealed record ServerBackupInfo(string Directory, ServerBackupKind Kind, DateTime CreatedLocal);

/// <summary>
/// On-demand backups from the Server tab, kept under <c>backups/manual/&lt;timestamp&gt;-&lt;kind&gt;</c>.
/// They are separate from the single pre-update backup the updater rotates, and are never deleted
/// by the launcher. Databases are dumped with a consistent snapshot, so the realm may keep running.
/// </summary>
public sealed class ServerBackupService
{
    public const string InfoFileName = "BACKUP_INFO.txt";
    private const string StampFormat = "yyyy-MM-dd_HH-mm-ss";

    private readonly LauncherPaths _paths;
    private readonly ServerManager _server;
    private readonly IServerDatabaseBackup _database;
    private readonly Func<string?> _installedVersion;

    public ServerBackupService(
        LauncherPaths paths,
        ServerManager server,
        IServerDatabaseBackup database,
        Func<string?> installedVersion)
    {
        _paths = paths;
        _server = server;
        _database = database;
        _installedVersion = installedVersion;
    }

    public string Root => Path.Combine(_paths.Backups, "manual");

    /// <summary>The newest manual backup, if any.</summary>
    public ServerBackupInfo? Latest => List().FirstOrDefault();

    /// <summary>Manual backups, newest first.</summary>
    public IReadOnlyList<ServerBackupInfo> List()
    {
        if (!Directory.Exists(Root))
            return [];
        return Directory.EnumerateDirectories(Root)
            .Select(TryDescribe)
            .OfType<ServerBackupInfo>()
            .OrderByDescending(b => b.CreatedLocal)
            .ToList();
    }

    public async Task<ServerBackupInfo> CreateAsync(ServerBackupKind kind, CancellationToken ct = default)
    {
        var created = DateTime.Now;
        var dir = Path.Combine(Root, $"{created.ToString(StampFormat, System.Globalization.CultureInfo.InvariantCulture)}-{Suffix(kind)}");
        if (Directory.Exists(dir))
            throw new InvalidOperationException("A backup was just made. Wait a second and try again.");
        await _server.RunExclusiveAsync(async () =>
        {
            try
            {
                Directory.CreateDirectory(dir);
                if (kind == ServerBackupKind.Full)
                {
                    await Task.Run(() =>
                    {
                        CopyIfPresent(_paths.ServerBinaries, Path.Combine(dir, "server"), ct);
                        CopyIfPresent(_paths.Sql, Path.Combine(dir, "sql"), ct);
                        CopyIfPresent(_paths.Conf, Path.Combine(dir, "conf"), ct);
                        if (File.Exists(_paths.PortableLocalEnv))
                            File.Copy(_paths.PortableLocalEnv, Path.Combine(dir, "portable.local.env"));
                    }, ct).ConfigureAwait(false);
                }

                var databases = kind == ServerBackupKind.Full ? null : MariaDbDatabaseBackup.PlayerDatabases;
                await _database.BackupAsync(Path.Combine(dir, "sqldump"), databases, ct).ConfigureAwait(false);

                File.WriteAllText(Path.Combine(dir, InfoFileName),
                    $"kind={Suffix(kind)}\n" +
                    $"created={created:O}\n" +
                    $"server_version={_installedVersion() ?? "unknown"}\n" +
                    "restore=Import sqldump/*.sql with the MariaDB client; each dump recreates its database.\n");
            }
            catch
            {
                // A half-written backup is worse than none: it looks restorable but is not.
                try { Directory.Delete(dir, recursive: true); } catch { }
                throw;
            }
        }, ct).ConfigureAwait(false);

        return new ServerBackupInfo(dir, kind, created);
    }

    private static void CopyIfPresent(string source, string dest, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (Directory.Exists(source))
            ServerUtil.CopyDirectory(source, dest);
    }

    private static string Suffix(ServerBackupKind kind) => kind == ServerBackupKind.Full ? "full" : "characters";

    private static ServerBackupInfo? TryDescribe(string dir)
    {
        var name = Path.GetFileName(dir);
        ServerBackupKind kind;
        if (name.EndsWith("-full", StringComparison.OrdinalIgnoreCase))
            kind = ServerBackupKind.Full;
        else if (name.EndsWith("-characters", StringComparison.OrdinalIgnoreCase))
            kind = ServerBackupKind.Characters;
        else
            return null;
        if (!File.Exists(Path.Combine(dir, InfoFileName)))
            return null;
        var created = name.Length >= StampFormat.Length
                      && DateTime.TryParseExact(name[..StampFormat.Length], StampFormat,
                          System.Globalization.CultureInfo.InvariantCulture,
                          System.Globalization.DateTimeStyles.None, out var stamp)
            ? stamp
            : Directory.GetCreationTime(dir);
        return new ServerBackupInfo(dir, kind, created);
    }
}
