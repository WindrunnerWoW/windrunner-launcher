using WindrunnerLauncher.Core.Downloads;
using WindrunnerLauncher.Core.Models;
using WindrunnerLauncher.Core.Persistence;
using WindrunnerLauncher.Core.Realms;
using WindrunnerLauncher.Core.Security;

namespace WindrunnerLauncher.Core.Client;

/// <summary>
/// Restores managed assets on behalf of <see cref="ClientManager"/>. Implemented by
/// <c>ModManager</c>; kept as an interface so the client subsystem does not depend on the
/// manifest/handler machinery.
/// </summary>
public interface IManagedAssetRepairer
{
    Task ApplyRequiredAsync(RealmEntry realm, CancellationToken ct = default);
    Task RestoreManagedStateAsync(RealmEntry realm, bool strict, CancellationToken ct = default);
}

public sealed class ClientRepairReport
{
    public bool Deep { get; init; }
    public string ClientDirectory { get; init; } = "";
    public List<string> Actions { get; } = [];
    public List<string> Warnings { get; } = [];
}

public sealed class ClientManager
{
    /// <summary>Base game archives shipped with the client. These are never toggled or moved.</summary>
    public static readonly IReadOnlyList<string> OfficialMpqNames =
    [
        "backup", "base", "dbc", "fonts", "interface", "misc", "model",
        "patch", "patch-1", "patch-2", "patch-3", "patch-4", "patch-5", "patch-6",
        "patch-7", "patch-8", "patch-9",
        "sound", "speech", "terrain", "texture", "wmo"
    ];

    /// <summary>Executable names accepted as proof of a real client directory.</summary>
    public static readonly IReadOnlyList<string> ExecutableNames = ["WoW.exe", "Wow.exe", "WoW"];

    public const string CleanBackupFileName = "WoW-OriginalBackup.exe";

    private static readonly HashSet<string> OfficialMpqSet =
        new(OfficialMpqNames, StringComparer.OrdinalIgnoreCase);

    private readonly LauncherPaths _paths;
    private readonly StateStore _state;

    public event Action? Changed;

    public ClientManager(LauncherPaths paths, StateStore state)
    {
        _paths = paths;
        _state = state;
    }

    /// <summary>A directory is a client when it holds one of the known game executables.</summary>
    public bool IsValid(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path))
            return false;
        return FindExecutable(path) is not null;
    }

    public static string? FindExecutable(string directory)
    {
        foreach (var name in ExecutableNames)
        {
            var candidate = Path.Combine(directory, name);
            if (File.Exists(candidate))
                return candidate;
        }

        // Case-insensitive fallback for case-sensitive file systems.
        foreach (var file in EnumerateFilesSafe(directory))
        {
            var name = Path.GetFileName(file);
            if (ExecutableNames.Any(e => string.Equals(e, name, StringComparison.OrdinalIgnoreCase)))
                return file;
        }

        return null;
    }

    public static bool IsOfficialMpq(string fileNameOrPath)
    {
        var name = Path.GetFileNameWithoutExtension(fileNameOrPath);
        return OfficialMpqSet.Contains(name);
    }

    /// <summary>
    /// The server's own client patch. The Local Server needs it, so it is forced on there and can't
    /// be turned off. Other realms keep their own choice.
    /// </summary>
    public const string LocalServerPatchName = "patch-W";

    /// <summary>True when the realm cannot run without this archive, so it is always on and never toggled.</summary>
    public static bool IsRequiredFor(RealmEntry realm, string fileNameOrPath)
    {
        if (IsOfficialMpq(fileNameOrPath))
            return true;
        return RealmManager.IsLocal(realm)
               && string.Equals(Path.GetFileNameWithoutExtension(fileNameOrPath), LocalServerPatchName, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Directory the launcher currently treats as "the client".</summary>
    public string ResolveClientPath()
    {
        var configured = _state.Settings.ClientPath;
        if (!string.IsNullOrWhiteSpace(configured))
            return Path.GetFullPath(configured);
        return _paths.Client;
    }

    public string ClientForRealm(RealmEntry realm)
    {
        if (!string.IsNullOrWhiteSpace(realm.ClientDirectoryOverride))
            return Path.GetFullPath(realm.ClientDirectoryOverride);
        return ResolveClientPath();
    }

    /// <summary>
    /// Adopt an existing installation. With <paramref name="createManagedCopy"/> the tree is copied
    /// into <c>client/</c>; otherwise the source directory is managed in place.
    /// </summary>
    public async Task ImportAsync(string sourceDir, bool createManagedCopy, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(sourceDir) || !Directory.Exists(sourceDir))
            throw new DirectoryNotFoundException($"Client source directory not found: {sourceDir}");
        if (!IsValid(sourceDir))
            throw new InvalidOperationException($"No WoW executable found in {sourceDir}.");

        var full = Path.GetFullPath(sourceDir);
        if (!createManagedCopy)
        {
            _state.Settings.ClientPath = full;
            _state.Settings.ClientIsManagedCopy = false;
            _state.SaveSettings();
            EnsureCleanExecutableBackup(full);
            Changed?.Invoke();
            return;
        }

        var target = _paths.Client;
        if (string.Equals(full.TrimEnd(Path.DirectorySeparatorChar),
                Path.GetFullPath(target).TrimEnd(Path.DirectorySeparatorChar),
                StringComparison.OrdinalIgnoreCase))
        {
            _state.Settings.ClientPath = target;
            _state.Settings.ClientIsManagedCopy = true;
            _state.SaveSettings();
            EnsureCleanExecutableBackup(target);
            Changed?.Invoke();
            return;
        }

        await Task.Run(() => CopyTree(full, target, ct), ct).ConfigureAwait(false);
        _state.Settings.ClientPath = target;
        _state.Settings.ClientIsManagedCopy = true;
        _state.SaveSettings();
        EnsureCleanExecutableBackup(target);
        Changed?.Invoke();
    }

    /// <summary>Point the launcher back at the managed <c>client/</c> directory.</summary>
    public void UseManagedClient()
    {
        _state.Settings.ClientPath = _paths.Client;
        _state.Settings.ClientIsManagedCopy = true;
        _state.SaveSettings();
        if (IsValid(_paths.Client))
            EnsureCleanExecutableBackup(_paths.Client);
        Changed?.Invoke();
    }

    /// <summary>True when the manifest describes a base client the launcher can download and verify.</summary>
    public static bool CanBootstrap(ClientManifest manifest)
    {
        var bootstrap = manifest.Bootstrap;
        return bootstrap is not null
               && !string.IsNullOrWhiteSpace(bootstrap.Url)
               && bootstrap.Sha256.Length == 64
               && bootstrap.Sha256.All(Uri.IsHexDigit);
    }

    /// <summary>
    /// Download the known clean base client described by the manifest, verify it, extract it and
    /// move the nested game root into <c>client/</c>. This only ever establishes a base install;
    /// it is never used as an update mechanism.
    /// </summary>
    public async Task<string> BootstrapAsync(
        ClientManifest manifest,
        DownloadManager downloads,
        CancellationToken ct = default,
        Action<string>? status = null)
    {
        var bootstrap = manifest.Bootstrap
                        ?? throw new InvalidOperationException("The client manifest has no bootstrap entry.");
        if (string.IsNullOrWhiteSpace(bootstrap.Url))
            throw new InvalidOperationException("The client manifest bootstrap entry has no download URL.");
        if (bootstrap.Sha256.Length != 64 || !bootstrap.Sha256.All(Uri.IsHexDigit))
            throw new InvalidOperationException("The client manifest bootstrap entry has no valid SHA-256 checksum.");

        Directory.CreateDirectory(_paths.DownloadCache);
        var archive = Path.Combine(_paths.DownloadCache, "client-bootstrap.zip");

        var reusable = File.Exists(archive) && Checksums.VerifyFile(archive, bootstrap.Sha256);
        if (!reusable)
        {
            await downloads.DownloadAsync(new DownloadRequest
            {
                Id = "client-bootstrap",
                DisplayName = bootstrap.DisplayName,
                Url = bootstrap.Url,
                DestinationPath = archive,
                ExpectedSha256 = bootstrap.Sha256
            }, ct).ConfigureAwait(false);
        }

        if (!Checksums.VerifyFile(archive, bootstrap.Sha256))
            throw new InvalidDataException("Downloaded base client failed SHA-256 verification.");

        status?.Invoke("Extracting the client… this can take several minutes.");
        var staging = Path.Combine(_paths.Cache, "client-bootstrap");
        await Task.Run(() =>
        {
            ct.ThrowIfCancellationRequested();
            if (Directory.Exists(staging))
                Directory.Delete(staging, recursive: true);
            ArchiveUtil.ExtractZip(archive, staging);

            var root = ResolveBootstrapRoot(staging, bootstrap.NestedRoot)
                       ?? throw new InvalidDataException("Extracted base client contains no WoW executable.");

            Directory.CreateDirectory(_paths.Client);
            status?.Invoke("Moving the client into place…");
            MergeTree(root, _paths.Client, ct);
            Directory.Delete(staging, recursive: true);
        }, ct).ConfigureAwait(false);

        try { File.Delete(archive); } catch { }

        _state.Settings.ClientPath = _paths.Client;
        _state.Settings.ClientIsManagedCopy = true;
        _state.Settings.InstalledClientManifestVersion = manifest.Version;
        _state.SaveSettings();
        EnsureCleanExecutableBackup(_paths.Client);
        Changed?.Invoke();
        return _paths.Client;
    }

    /// <summary>
    /// Keep a pristine copy of the shipped executable so executable patches (VanillaTweaks) can
    /// always be regenerated from a clean base instead of patching a patched binary.
    /// </summary>
    public string? EnsureCleanExecutableBackup(string clientDir)
    {
        var exe = FindExecutable(clientDir);
        if (exe is null)
            return null;

        var backup = ClientPaths.Child(clientDir, CleanBackupFileName);
        if (!File.Exists(backup))
        {
            backup = Path.Combine(clientDir, CleanBackupFileName);
            File.Copy(exe, backup, overwrite: false);
        }

        if (!string.Equals(_state.Settings.CleanWowExeBackupPath, backup, StringComparison.Ordinal))
        {
            _state.Settings.CleanWowExeBackupPath = backup;
            _state.SaveSettings();
        }

        return backup;
    }

    public string? CleanExecutableBackupFor(string clientDir)
    {
        var local = ClientPaths.Child(clientDir, CleanBackupFileName);
        if (File.Exists(local))
            return local;

        // The setting predates per-realm client directories. Never let a backup from a
        // different installation be used for this client.
        var configured = _state.Settings.CleanWowExeBackupPath;
        if (!string.IsNullOrWhiteSpace(configured) && File.Exists(configured)
            && string.Equals(Path.GetFullPath(Path.GetDirectoryName(configured) ?? "")
                           .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                Path.GetFullPath(clientDir).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                StringComparison.OrdinalIgnoreCase))
            return configured;
        return null;
    }

    /// <summary>
    /// Normal repair: re-apply required managed assets and rebuild launcher-owned scaffolding.
    /// Unknown files are preserved.
    /// </summary>
    public async Task<ClientRepairReport> RepairAsync(
        IManagedAssetRepairer mods,
        RealmEntry realm,
        CancellationToken ct = default)
        => await RepairCoreAsync(mods, realm, deep: false, ct).ConfigureAwait(false);

    /// <summary>
    /// Deep repair / reset: additionally forces every managed asset back to the realm's desired
    /// state, disabling managed extras that are live but not wanted. Unknown files still survive.
    /// </summary>
    public async Task<ClientRepairReport> DeepRepairAsync(
        IManagedAssetRepairer mods,
        RealmEntry realm,
        CancellationToken ct = default)
        => await RepairCoreAsync(mods, realm, deep: true, ct).ConfigureAwait(false);

    private async Task<ClientRepairReport> RepairCoreAsync(
        IManagedAssetRepairer mods,
        RealmEntry realm,
        bool deep,
        CancellationToken ct)
    {
        var clientDir = ClientForRealm(realm);
        var report = new ClientRepairReport { Deep = deep, ClientDirectory = clientDir };

        if (!IsValid(clientDir))
        {
            report.Warnings.Add($"No WoW executable in {clientDir}; nothing to repair.");
            return report;
        }

        foreach (var dir in new[] { "Data", "Interface", "Interface/AddOns", "WTF", "Logs" })
        {
            var full = ClientPaths.Resolve(clientDir, dir);
            if (!Directory.Exists(full))
            {
                Directory.CreateDirectory(full);
                report.Actions.Add($"Recreated missing directory {dir}.");
            }
        }

        if (EnsureCleanExecutableBackup(clientDir) is { } backup)
            report.Actions.Add($"Clean executable backup present: {Path.GetFileName(backup)}.");
        else
            report.Warnings.Add("Could not create a clean executable backup.");

        await mods.ApplyRequiredAsync(realm, ct).ConfigureAwait(false);
        report.Actions.Add("Re-applied required managed assets.");

        if (deep)
        {
            await mods.RestoreManagedStateAsync(realm, strict: true, ct).ConfigureAwait(false);
            report.Actions.Add("Restored managed asset state; managed extras were moved back to mods/.");
        }

        Changed?.Invoke();
        return report;
    }

    private static string? ResolveBootstrapRoot(string extracted, string nestedRoot)
    {
        if (!string.IsNullOrWhiteSpace(nestedRoot))
        {
            var hinted = Path.Combine(extracted, nestedRoot.Replace('\\', Path.DirectorySeparatorChar)
                .Replace('/', Path.DirectorySeparatorChar));
            if (Directory.Exists(hinted) && FindExecutable(hinted) is not null)
                return hinted;
        }

        foreach (var name in ExecutableNames)
        {
            if (ArchiveUtil.FindNestedRoot(extracted, name) is { } found)
                return found;
        }

        return null;
    }

    private static IEnumerable<string> EnumerateFilesSafe(string directory)
    {
        try
        {
            return Directory.EnumerateFiles(directory);
        }
        catch
        {
            return [];
        }
    }

    private static void CopyTree(string source, string dest, CancellationToken ct)
    {
        Directory.CreateDirectory(dest);
        foreach (var dir in Directory.EnumerateDirectories(source, "*", SearchOption.AllDirectories))
        {
            ct.ThrowIfCancellationRequested();
            Directory.CreateDirectory(Rebase(source, dest, dir));
        }

        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            ct.ThrowIfCancellationRequested();
            var target = Rebase(source, dest, file);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target, overwrite: true);
        }
    }

    /// <summary>Moves a tree onto an existing directory, overwriting files but keeping extras.</summary>
    private static void MergeTree(string source, string dest, CancellationToken ct)
    {
        Directory.CreateDirectory(dest);
        foreach (var dir in Directory.EnumerateDirectories(source, "*", SearchOption.AllDirectories))
        {
            ct.ThrowIfCancellationRequested();
            Directory.CreateDirectory(Rebase(source, dest, dir));
        }

        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            ct.ThrowIfCancellationRequested();
            var target = Rebase(source, dest, file);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            if (File.Exists(target))
                File.Delete(target);
            File.Move(file, target);
        }
    }

    private static string Rebase(string source, string dest, string path)
    {
        var relative = Path.GetRelativePath(source, path);
        return Path.Combine(dest, relative);
    }
}
