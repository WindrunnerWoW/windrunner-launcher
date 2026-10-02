using WindrunnerLauncher.Core.Downloads;
using WindrunnerLauncher.Core.Models;
using WindrunnerLauncher.Core.Persistence;

namespace WindrunnerLauncher.Core.Updates;

public sealed class BackupRollbackManager
{
    private readonly LauncherPaths _paths;
    private readonly StateStore _state;

    public BackupRollbackManager(LauncherPaths paths, StateStore state)
    {
        _paths = paths;
        _state = state;
    }

    public bool HasBackup => _state.Rollback is not null && Directory.Exists(_state.Rollback.Directory);

    /// <summary>The backup being held, or null when <see cref="HasBackup"/> is false.</summary>
    public RollbackMetadata? Current => HasBackup ? _state.Rollback : null;

    /// <summary>Where the pre-update database dumps (<c>&lt;database&gt;.sql</c>) are kept.</summary>
    public string DumpDirectory => Path.Combine(_state.Rollback?.Directory ?? _paths.ServerBackupDir, "sqldump");

    public async Task CreatePreUpdateBackupAsync(string fromVersion, string toVersion, CancellationToken ct = default)
    {
        await Task.Run(() =>
        {
            ct.ThrowIfCancellationRequested();
            if (Directory.Exists(_paths.ServerBackupDir))
                Directory.Delete(_paths.ServerBackupDir, recursive: true);
            Directory.CreateDirectory(_paths.ServerBackupDir);
            CopyTree(_paths.ServerBinaries, Path.Combine(_paths.ServerBackupDir, "server"));
            CopyTree(_paths.Sql, Path.Combine(_paths.ServerBackupDir, "sql"));
            CopyTree(_paths.Conf, Path.Combine(_paths.ServerBackupDir, "conf"));
            var env = Path.Combine(_paths.ServerBackupDir, "portable.local.env");
            if (File.Exists(_paths.PortableLocalEnv))
                File.Copy(_paths.PortableLocalEnv, env, overwrite: true);
            foreach (var marker in ReleaseMarkers())
            {
                if (File.Exists(marker))
                    File.Copy(marker, Path.Combine(_paths.ServerBackupDir, Path.GetFileName(marker)), overwrite: true);
            }
            var dumpDir = Path.Combine(_paths.ServerBackupDir, "sqldump");
            Directory.CreateDirectory(dumpDir);
            _state.SaveRollback(new RollbackMetadata
            {
                BackupId = DateTime.UtcNow.ToString("yyyyMMddHHmmss"),
                FromVersion = fromVersion,
                ToVersion = toVersion,
                CreatedUtc = DateTime.UtcNow.ToString("O"),
                Directory = _paths.ServerBackupDir
            });
        }, ct).ConfigureAwait(false);
    }

    /// <summary>Restores the backed-up files, then discards the backup.</summary>
    public async Task RestoreAsync(CancellationToken ct = default)
    {
        await RestoreFilesAsync(ct).ConfigureAwait(false);
        DeleteBackup();
    }

    /// <summary>
    /// Restores <c>server/</c>, <c>sql/</c>, <c>conf/</c> and the local env from the backup but keeps
    /// the backup, so the caller can still restore the database dumps from <see cref="DumpDirectory"/>.
    /// </summary>
    public async Task RestoreFilesAsync(CancellationToken ct = default)
    {
        var meta = _state.Rollback ?? throw new InvalidOperationException("No rollback backup.");
        await Task.Run(() =>
        {
            ct.ThrowIfCancellationRequested();
            if (!Directory.Exists(meta.Directory))
                throw new DirectoryNotFoundException(meta.Directory);
            ReplaceDir(Path.Combine(meta.Directory, "server"), _paths.ServerBinaries);
            ReplaceDir(Path.Combine(meta.Directory, "sql"), _paths.Sql);
            ReplaceDir(Path.Combine(meta.Directory, "conf"), _paths.Conf);
            var env = Path.Combine(meta.Directory, "portable.local.env");
            if (File.Exists(env))
                File.Copy(env, _paths.PortableLocalEnv, overwrite: true);
            foreach (var marker in ReleaseMarkers())
            {
                // No saved marker means there was none before the update; a newer one must not survive.
                var saved = Path.Combine(meta.Directory, Path.GetFileName(marker));
                if (File.Exists(saved))
                    File.Copy(saved, marker, overwrite: true);
                else if (File.Exists(marker))
                    File.Delete(marker);
            }
        }, ct).ConfigureAwait(false);
    }

    private IEnumerable<string> ReleaseMarkers() => [_paths.ServerReleaseMarker, _paths.SqlReleaseMarker];

    public void DeleteBackup()
    {
        if (Directory.Exists(_paths.ServerBackupDir))
            Directory.Delete(_paths.ServerBackupDir, recursive: true);
        _state.SaveRollback(null);
    }

    private static void ReplaceDir(string source, string dest)
    {
        if (!Directory.Exists(source))
            return;
        if (Directory.Exists(dest))
            Directory.Delete(dest, recursive: true);
        CopyTree(source, dest);
    }

    private static void CopyTree(string source, string dest)
    {
        if (!Directory.Exists(source))
            return;
        Directory.CreateDirectory(dest);
        foreach (var dir in Directory.EnumerateDirectories(source, "*", SearchOption.AllDirectories))
        {
            Directory.CreateDirectory(dir.Replace(source, dest));
        }

        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            var target = file.Replace(source, dest);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target, overwrite: true);
        }
    }
}
