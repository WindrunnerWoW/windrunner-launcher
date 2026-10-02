using WindrunnerLauncher.Core.Persistence;
using WindrunnerLauncher.Core.Server;
using WindrunnerLauncher.Core.Updates;

namespace WindrunnerLauncher.Core.Tests;

public class ConsoleCaptureTests
{
    [Fact]
    public void Append_NormalizesLineEndings()
    {
        var c = new ConsoleCapture();
        c.Append("a\r\nb\rc\n");
        c.Append(null);
        c.Append("");
        Assert.Equal("a\nb\nc\n", c.Text);
        Assert.True(c.Contains("B"));
        Assert.False(c.Contains("B", StringComparison.Ordinal));
    }

    [Fact]
    public void Append_TrimsToRingSize_AtLineBoundary()
    {
        var c = new ConsoleCapture();
        var line = new string('x', 1023) + "\n";
        for (var i = 0; i < 300; i++)
            c.Append(line);
        c.Append("tail\n");

        var text = c.Text;
        Assert.True(text.Length <= ConsoleCapture.MaxChars);
        Assert.StartsWith("x", text);
        Assert.EndsWith("tail\n", text);
        Assert.Equal(0, text.Split('\n', StringSplitOptions.RemoveEmptyEntries).Count(l => l.Length != 1023 && l != "tail"));
    }

    [Fact]
    public void Clear_Empties()
    {
        var c = new ConsoleCapture();
        c.Append("x");
        c.Clear();
        Assert.Equal("", c.Text);
    }
}

public class BackupRollbackManagerTests
{
    [Fact]
    public async Task CreateBackup_ThenRestore_RoundTripsServerTree()
    {
        using var tmp = new TempDir();
        var paths = tmp.Paths();
        paths.EnsureLayout();
        var state = new StateStore(paths);
        var manager = new BackupRollbackManager(paths, state);
        Assert.False(manager.HasBackup);

        File.WriteAllText(Path.Combine(paths.ServerBinaries, "mangosd.exe"), "v1");
        File.WriteAllText(Path.Combine(paths.Sql, "world.sql"), "v1-sql");
        File.WriteAllText(Path.Combine(paths.Conf, "my.ini"), "v1-ini");
        File.WriteAllText(paths.PortableLocalEnv, "REALM_NAME=v1\n");

        await manager.CreatePreUpdateBackupAsync("1.0", "1.1");

        Assert.True(manager.HasBackup);
        Assert.NotNull(state.Rollback);
        Assert.Equal("1.0", state.Rollback!.FromVersion);
        Assert.Equal("1.1", state.Rollback.ToVersion);
        Assert.Equal(paths.ServerBackupDir, state.Rollback.Directory);
        Assert.True(File.Exists(paths.RollbackFile));
        Assert.Equal("v1", File.ReadAllText(Path.Combine(paths.ServerBackupDir, "server", "mangosd.exe")));

        File.WriteAllText(Path.Combine(paths.ServerBinaries, "mangosd.exe"), "v2");
        File.WriteAllText(Path.Combine(paths.ServerBinaries, "new-file.dll"), "v2");
        File.WriteAllText(Path.Combine(paths.Sql, "world.sql"), "v2-sql");
        File.WriteAllText(paths.PortableLocalEnv, "REALM_NAME=v2\n");

        await manager.RestoreAsync();

        Assert.Equal("v1", File.ReadAllText(Path.Combine(paths.ServerBinaries, "mangosd.exe")));
        Assert.False(File.Exists(Path.Combine(paths.ServerBinaries, "new-file.dll")), "restore replaces the directory");
        Assert.Equal("v1-sql", File.ReadAllText(Path.Combine(paths.Sql, "world.sql")));
        Assert.Equal("v1-ini", File.ReadAllText(Path.Combine(paths.Conf, "my.ini")));
        Assert.Equal("REALM_NAME=v1\n", File.ReadAllText(paths.PortableLocalEnv));
        Assert.False(manager.HasBackup);
        Assert.Null(state.Rollback);
        Assert.False(File.Exists(paths.RollbackFile));
        Assert.False(Directory.Exists(paths.ServerBackupDir));
    }

    [Fact]
    public async Task CreateBackup_ReplacesPreviousBackup()
    {
        using var tmp = new TempDir();
        var paths = tmp.Paths();
        paths.EnsureLayout();
        var state = new StateStore(paths);
        var manager = new BackupRollbackManager(paths, state);

        File.WriteAllText(Path.Combine(paths.ServerBinaries, "a.txt"), "first");
        await manager.CreatePreUpdateBackupAsync("1", "2");
        File.Delete(Path.Combine(paths.ServerBinaries, "a.txt"));
        File.WriteAllText(Path.Combine(paths.ServerBinaries, "b.txt"), "second");
        await manager.CreatePreUpdateBackupAsync("2", "3");

        Assert.False(File.Exists(Path.Combine(paths.ServerBackupDir, "server", "a.txt")));
        Assert.True(File.Exists(Path.Combine(paths.ServerBackupDir, "server", "b.txt")));
        Assert.Equal("2", state.Rollback!.FromVersion);
    }

    [Fact]
    public async Task Restore_WithoutBackup_Throws()
    {
        using var tmp = new TempDir();
        var paths = tmp.Paths();
        var manager = new BackupRollbackManager(paths, new StateStore(paths));
        await Assert.ThrowsAsync<InvalidOperationException>(() => manager.RestoreAsync());
    }

    [Fact]
    public async Task Restore_WithMissingBackupDirectory_Throws()
    {
        using var tmp = new TempDir();
        var paths = tmp.Paths();
        var state = new StateStore(paths);
        state.SaveRollback(new RollbackMetadata { Directory = tmp.Combine("gone") });
        var manager = new BackupRollbackManager(paths, state);
        Assert.False(manager.HasBackup);
        await Assert.ThrowsAsync<DirectoryNotFoundException>(() => manager.RestoreAsync());
    }

    [Fact]
    public async Task DeleteBackup_RemovesDirectoryAndMetadata()
    {
        using var tmp = new TempDir();
        var paths = tmp.Paths();
        paths.EnsureLayout();
        var state = new StateStore(paths);
        var manager = new BackupRollbackManager(paths, state);
        await manager.CreatePreUpdateBackupAsync("1", "2");
        Assert.True(manager.HasBackup);

        manager.DeleteBackup();
        Assert.False(manager.HasBackup);
        Assert.Null(state.Rollback);
        Assert.False(Directory.Exists(paths.ServerBackupDir));
        manager.DeleteBackup();
    }

    [Fact]
    public async Task CreateBackup_Cancelled_Throws()
    {
        using var tmp = new TempDir();
        var paths = tmp.Paths();
        paths.EnsureLayout();
        var manager = new BackupRollbackManager(paths, new StateStore(paths));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            manager.CreatePreUpdateBackupAsync("1", "2", new CancellationToken(canceled: true)));
    }
}
