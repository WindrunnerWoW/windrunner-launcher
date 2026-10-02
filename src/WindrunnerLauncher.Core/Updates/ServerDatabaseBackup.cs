using WindrunnerLauncher.Core.Server;

namespace WindrunnerLauncher.Core.Updates;

/// <summary>Dumps and restores the realm databases around a server update.</summary>
public interface IServerDatabaseBackup
{
    /// <summary>
    /// Writes one <c>&lt;database&gt;.sql</c> into <paramref name="directory"/> for each of
    /// <paramref name="databases"/> that exists (every realm database when null).
    /// </summary>
    Task BackupAsync(string directory, IReadOnlyList<string>? databases, CancellationToken ct);

    /// <summary>Re-imports every dump in <paramref name="directory"/>, replacing the live databases.</summary>
    Task RestoreAsync(string directory, CancellationToken ct);
}

/// <summary>
/// mariadb-dump based backup of the portable MariaDB. Starts MariaDB for the duration when the
/// realm is stopped, and stops it again afterwards.
/// </summary>
public sealed class MariaDbDatabaseBackup : IServerDatabaseBackup
{
    /// <summary>Accounts, characters, world, and logs, in the order mangosd's confs name them.</summary>
    public static readonly IReadOnlyList<string> Databases = ["tw_logon", "tw_char", "tw_world", "tw_logs"];

    /// <summary>What a player owns: accounts (<c>tw_logon</c>) and characters (<c>tw_char</c>).</summary>
    public static readonly IReadOnlyList<string> PlayerDatabases = ["tw_logon", "tw_char"];

    private readonly MariaDbManager _maria;

    public MariaDbDatabaseBackup(MariaDbManager maria)
    {
        _maria = maria ?? throw new ArgumentNullException(nameof(maria));
    }

    public Task BackupAsync(string directory, IReadOnlyList<string>? databases, CancellationToken ct) => WithDatabaseAsync(async () =>
    {
        Directory.CreateDirectory(directory);
        var present = (await _maria.InvokeSqlAsync("SHOW DATABASES;", ct).ConfigureAwait(false))
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var database in (databases ?? Databases).Where(present.Contains))
        {
            ct.ThrowIfCancellationRequested();
            await _maria.DumpDatabaseAsync(database, Path.Combine(directory, database + ".sql"), ct).ConfigureAwait(false);
        }
    }, ct);

    public Task RestoreAsync(string directory, CancellationToken ct) => WithDatabaseAsync(async () =>
    {
        foreach (var database in Databases)
        {
            ct.ThrowIfCancellationRequested();
            var dump = Path.Combine(directory, database + ".sql");
            // Dumps are written with --databases --add-drop-database, so each one recreates its database.
            if (File.Exists(dump))
                await _maria.InvokeSqlFileAsync(dump, ct: ct).ConfigureAwait(false);
        }
    }, ct);

    private async Task WithDatabaseAsync(Func<Task> work, CancellationToken ct)
    {
        var wasReady = _maria.IsReady;
        if (!wasReady)
            await _maria.StartAsync(ct).ConfigureAwait(false);
        try
        {
            await work().ConfigureAwait(false);
        }
        finally
        {
            if (!wasReady)
            {
                try { await _maria.StopAsync(CancellationToken.None).ConfigureAwait(false); }
                catch { /* the next start reports a stuck daemon */ }
            }
        }
    }
}
