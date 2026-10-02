using WindrunnerLauncher.Core.Client;
using WindrunnerLauncher.Core.Models;
using WindrunnerLauncher.Core.Persistence;
using WindrunnerLauncher.Core.Platform;
using WindrunnerLauncher.Core.Security;

namespace WindrunnerLauncher.Core.Mods;

public sealed class ModApplyContext
{
    public required LauncherPaths Paths { get; init; }
    public required StateStore State { get; init; }
    public required ManagedAsset Asset { get; init; }

    /// <summary>Live client directory for the realm being materialized.</summary>
    public required string ClientDir { get; init; }

    /// <summary>Off-client store for this asset: <c>mods/&lt;assetId&gt;</c>.</summary>
    public required string StoreDir { get; init; }

    public string? CleanExecutableBackup { get; init; }

    public string LivePath =>
        string.IsNullOrWhiteSpace(Asset.Destination)
            ? ClientDir
            : ClientPaths.Resolve(ClientDir, Asset.Destination);

    public string LiveName => ModPaths.LeafName(Asset.Destination);
}

public sealed class ModApplyOutcome
{
    public bool Changed { get; init; }
    public bool Skipped { get; init; }
    public string Message { get; init; } = "";

    public static ModApplyOutcome Ok(string message) => new() { Changed = true, Message = message };
    public static ModApplyOutcome NoChange(string message) => new() { Message = message };
    public static ModApplyOutcome Skip(string message) => new() { Skipped = true, Message = message };
}

public interface IModHandler
{
    ModKind Kind { get; }

    /// <summary>True when the asset is currently live in the client directory.</summary>
    bool IsApplied(ModApplyContext ctx);

    /// <summary>True when the payload is available offline (either live or in <c>mods/</c>).</summary>
    bool IsInstalled(ModApplyContext ctx);

    Task<ModApplyOutcome> EnableAsync(ModApplyContext ctx, CancellationToken ct = default);
    Task<ModApplyOutcome> DisableAsync(ModApplyContext ctx, CancellationToken ct = default);
}

public static class ModHandlers
{
    private static readonly Dictionary<ModKind, IModHandler> Registry = new()
    {
        [ModKind.Mpq] = new MpqHandler(),
        [ModKind.Dll] = new DllHandler(),
        [ModKind.AddOn] = new AddOnHandler(),
        [ModKind.ZipRoot] = new ZipRootHandler(),
        [ModKind.ExecutablePatch] = new ExecutablePatchHandler(),
        [ModKind.Configuration] = new ConfigurationHandler(),
        [ModKind.Dxvk] = new DxvkHandler()
    };

    public static IModHandler For(ModKind kind) =>
        Registry.TryGetValue(kind, out var handler) ? handler : UnsupportedHandler.Instance;

    public static bool IsSupported(ModKind kind) => Registry.ContainsKey(kind);
}

/// <summary>Path helpers shared by the handlers. Manifest destinations always use forward slashes.</summary>
public static class ModPaths
{
    public static string Normalize(string destination) =>
        destination.Replace('\\', '/').Trim('/').Replace('/', Path.DirectorySeparatorChar);

    public static string LeafName(string destination)
    {
        var normalized = Normalize(destination);
        return normalized.Length == 0 ? "" : Path.GetFileName(normalized);
    }
}

internal static class ModFileOps
{
    public static void MoveFile(string source, string destination)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        if (File.Exists(destination))
            File.Delete(destination);
        File.Move(source, destination);
    }

    public static void MoveDirectory(string source, string destination)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        if (Directory.Exists(destination))
            Directory.Delete(destination, recursive: true);
        try
        {
            Directory.Move(source, destination);
        }
        catch (IOException)
        {
            // Cross-volume moves are not supported by Directory.Move.
            CopyTree(source, destination);
            Directory.Delete(source, recursive: true);
        }
    }

    public static void CopyTree(string source, string destination)
    {
        Directory.CreateDirectory(destination);
        foreach (var dir in Directory.EnumerateDirectories(source, "*", SearchOption.AllDirectories))
            Directory.CreateDirectory(Path.Combine(destination, Path.GetRelativePath(source, dir)));
        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            var target = Path.Combine(destination, Path.GetRelativePath(source, file));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target, overwrite: true);
        }
    }

    public static void DeleteEmptyParents(string startDirectory, string stopAt)
    {
        var stop = Path.GetFullPath(stopAt);
        var dir = Path.GetFullPath(startDirectory);
        while (dir.Length > stop.Length && dir.StartsWith(stop, StringComparison.OrdinalIgnoreCase))
        {
            if (!Directory.Exists(dir) || Directory.EnumerateFileSystemEntries(dir).Any())
                return;
            Directory.Delete(dir);
            dir = Path.GetDirectoryName(dir) ?? stop;
        }
    }

    /// <summary>
    /// Resolves a payload inside <c>mods/&lt;assetId&gt;</c>. Downloads arrive as archives, so any
    /// zip in the store is unpacked once and then searched for the expected entry.
    /// </summary>
    public static string? ResolveStoreEntry(string storeDir, string name, bool directory)
    {
        if (!Directory.Exists(storeDir) || string.IsNullOrEmpty(name))
            return null;

        if (Find(storeDir, name, directory) is { } direct)
            return direct;

        foreach (var zip in Directory.EnumerateFiles(storeDir, "*.zip", SearchOption.TopDirectoryOnly))
        {
            var marker = zip + ".extracted";
            if (File.Exists(marker))
                continue;
            try
            {
                ArchiveUtil.ExtractZip(zip, storeDir);
                File.WriteAllText(marker, DateTime.UtcNow.ToString("O"));
            }
            catch (InvalidDataException)
            {
                // Not a usable archive; leave it for the caller to report.
            }
        }

        return Find(storeDir, name, directory);
    }

    private static string? Find(string storeDir, string name, bool directory)
    {
        var direct = Path.Combine(storeDir, name);
        if (directory ? Directory.Exists(direct) : File.Exists(direct))
            return direct;

        var matches = directory
            ? Directory.EnumerateDirectories(storeDir, name, SearchOption.AllDirectories)
            : Directory.EnumerateFiles(storeDir, name, SearchOption.AllDirectories);
        return matches.FirstOrDefault();
    }
}

/// <summary>
/// Optional MPQ patches. Only letter patches (<c>patch-A.mpq</c> … <c>patch-Z.mpq</c>) may be
/// toggled; the base archives and the numbered official patches are off limits.
/// </summary>
public sealed class MpqHandler : IModHandler
{
    public ModKind Kind => ModKind.Mpq;

    public bool IsApplied(ModApplyContext ctx) => File.Exists(ctx.LivePath);

    public bool IsInstalled(ModApplyContext ctx) =>
        IsApplied(ctx) || ModFileOps.ResolveStoreEntry(ctx.StoreDir, ctx.LiveName, directory: false) is not null;

    public Task<ModApplyOutcome> EnableAsync(ModApplyContext ctx, CancellationToken ct = default)
    {
        Guard(ctx);
        if (IsApplied(ctx) && IsCurrent(ctx))
            return Task.FromResult(ModApplyOutcome.NoChange($"{ctx.LiveName} is already in Data/."));

        var source = ModFileOps.ResolveStoreEntry(ctx.StoreDir, ctx.LiveName, directory: false);
        if (source is null)
            return Task.FromResult(ModApplyOutcome.Skip($"{ctx.LiveName} is not present in mods/{ctx.Asset.Id}."));
        if (!IsPayloadValid(ctx, source))
            return Task.FromResult(ModApplyOutcome.Skip($"{ctx.LiveName} payload failed checksum verification."));

        ModFileOps.MoveFile(source, ctx.LivePath);
        return Task.FromResult(ModApplyOutcome.Ok($"Enabled {ctx.LiveName}."));
    }

    private static bool IsCurrent(ModApplyContext ctx) =>
        string.IsNullOrWhiteSpace(ctx.Asset.Sha256) || Checksums.VerifyFile(ctx.LivePath, ctx.Asset.Sha256);

    private static bool IsPayloadValid(ModApplyContext ctx, string source) =>
        string.IsNullOrWhiteSpace(ctx.Asset.Sha256) || Checksums.VerifyFile(source, ctx.Asset.Sha256);

    public Task<ModApplyOutcome> DisableAsync(ModApplyContext ctx, CancellationToken ct = default)
    {
        Guard(ctx);
        if (!IsApplied(ctx))
            return Task.FromResult(ModApplyOutcome.NoChange($"{ctx.LiveName} is not installed."));

        ModFileOps.MoveFile(ctx.LivePath, Path.Combine(ctx.StoreDir, ctx.LiveName));
        return Task.FromResult(ModApplyOutcome.Ok($"Disabled {ctx.LiveName}; moved to mods/{ctx.Asset.Id}."));
    }

    /// <summary>Rejects anything that would touch base game data.</summary>
    public static void Guard(ModApplyContext ctx)
    {
        var name = ctx.LiveName;
        if (name.Length == 0)
            throw new InvalidOperationException($"Asset {ctx.Asset.Id} has no MPQ destination.");
        if (ClientManager.IsOfficialMpq(name))
            throw new InvalidOperationException(
                $"{name} is an official client archive and is never toggled by the launcher.");
        if (!IsLetterPatch(name))
            throw new InvalidOperationException(
                $"{name} is not a letter patch (patch-A.mpq … patch-Z.mpq); only letter patches are managed.");
    }

    public static bool IsLetterPatch(string fileName)
    {
        var stem = Path.GetFileNameWithoutExtension(fileName);
        return stem.Length == 7
               && stem.StartsWith("patch-", StringComparison.OrdinalIgnoreCase)
               && char.IsAsciiLetter(stem[6]);
    }
}

/// <summary>Injected libraries that live in the client root and are listed in <c>dlls.txt</c>.</summary>
public sealed class DllHandler : IModHandler
{
    public ModKind Kind => ModKind.Dll;

    public bool IsApplied(ModApplyContext ctx) => File.Exists(ctx.LivePath);

    public bool IsInstalled(ModApplyContext ctx) =>
        IsApplied(ctx) || ModFileOps.ResolveStoreEntry(ctx.StoreDir, ctx.LiveName, directory: false) is not null;

    public Task<ModApplyOutcome> EnableAsync(ModApplyContext ctx, CancellationToken ct = default)
    {
        if (!IsApplied(ctx) || !IsCurrent(ctx))
        {
            var source = ModFileOps.ResolveStoreEntry(ctx.StoreDir, ctx.LiveName, directory: false);
            if (source is null)
                return Task.FromResult(ModApplyOutcome.Skip($"{ctx.LiveName} is not present in mods/{ctx.Asset.Id}."));
            if (!IsPayloadValid(ctx, source))
                return Task.FromResult(ModApplyOutcome.Skip($"{ctx.LiveName} payload failed checksum verification."));
            ModFileOps.MoveFile(source, ctx.LivePath);
        }

        foreach (var entry in DllEntries(ctx))
            DllsTxt.Add(ctx.ClientDir, entry);

        return Task.FromResult(ModApplyOutcome.Ok($"Enabled {ctx.LiveName}."));
    }

    private static bool IsCurrent(ModApplyContext ctx) =>
        string.IsNullOrWhiteSpace(ctx.Asset.Sha256) || Checksums.VerifyFile(ctx.LivePath, ctx.Asset.Sha256);

    private static bool IsPayloadValid(ModApplyContext ctx, string source) =>
        string.IsNullOrWhiteSpace(ctx.Asset.Sha256) || Checksums.VerifyFile(source, ctx.Asset.Sha256);

    public Task<ModApplyOutcome> DisableAsync(ModApplyContext ctx, CancellationToken ct = default)
    {
        foreach (var entry in DllEntries(ctx))
            DllsTxt.Remove(ctx.ClientDir, entry);

        if (!File.Exists(ctx.LivePath))
            return Task.FromResult(ModApplyOutcome.NoChange($"{ctx.LiveName} is not installed."));

        ModFileOps.MoveFile(ctx.LivePath, Path.Combine(ctx.StoreDir, ctx.LiveName));
        return Task.FromResult(ModApplyOutcome.Ok($"Disabled {ctx.LiveName}; moved to mods/{ctx.Asset.Id}."));
    }

    private static IEnumerable<string> DllEntries(ModApplyContext ctx) =>
        ctx.Asset.DllsTxtAdd.Count > 0 ? ctx.Asset.DllsTxtAdd : [];
}

/// <summary>Interface addons: a single folder under <c>Interface/AddOns</c>.</summary>
public sealed class AddOnHandler : IModHandler
{
    public ModKind Kind => ModKind.AddOn;

    public bool IsApplied(ModApplyContext ctx) => Directory.Exists(ctx.LivePath);

    public bool IsInstalled(ModApplyContext ctx) =>
        IsApplied(ctx) || ModFileOps.ResolveStoreEntry(ctx.StoreDir, ctx.LiveName, directory: true) is not null;

    public Task<ModApplyOutcome> EnableAsync(ModApplyContext ctx, CancellationToken ct = default)
    {
        if (IsApplied(ctx))
            return Task.FromResult(ModApplyOutcome.NoChange($"{ctx.LiveName} is already installed."));

        var source = ModFileOps.ResolveStoreEntry(ctx.StoreDir, ctx.LiveName, directory: true);
        if (source is null)
            return Task.FromResult(ModApplyOutcome.Skip($"AddOn {ctx.LiveName} is not present in mods/{ctx.Asset.Id}."));

        ModFileOps.MoveDirectory(source, ctx.LivePath);
        return Task.FromResult(ModApplyOutcome.Ok($"Enabled addon {ctx.LiveName}."));
    }

    public Task<ModApplyOutcome> DisableAsync(ModApplyContext ctx, CancellationToken ct = default)
    {
        if (!IsApplied(ctx))
            return Task.FromResult(ModApplyOutcome.NoChange($"AddOn {ctx.LiveName} is not installed."));

        ModFileOps.MoveDirectory(ctx.LivePath, Path.Combine(ctx.StoreDir, ctx.LiveName));
        return Task.FromResult(ModApplyOutcome.Ok($"Disabled addon {ctx.LiveName}; moved to mods/{ctx.Asset.Id}."));
    }
}

/// <summary>
/// Archives extracted over the client root (VanillaFixes and friends). The extracted file list is
/// recorded so disabling removes exactly what was added and nothing else.
/// </summary>
public sealed class ZipRootHandler : IModHandler
{
    public ModKind Kind => ModKind.ZipRoot;

    public bool IsApplied(ModApplyContext ctx)
    {
        if (FindManifestPath(ctx) is null)
            return false;
        return ReadManifest(ctx).Any(rel => File.Exists(Path.Combine(TargetRoot(ctx), rel)));
    }

    public bool IsInstalled(ModApplyContext ctx) => IsApplied(ctx) || FindArchive(ctx) is not null;

    public Task<ModApplyOutcome> EnableAsync(ModApplyContext ctx, CancellationToken ct = default)
    {
        if (IsApplied(ctx))
            return Task.FromResult(ModApplyOutcome.NoChange($"{ctx.Asset.Id} is already extracted into the client."));

        var archive = FindArchive(ctx);
        if (archive is null)
            return Task.FromResult(ModApplyOutcome.Skip($"No archive for {ctx.Asset.Id} in mods/{ctx.Asset.Id}."));

        var root = TargetRoot(ctx);
        var staging = Path.Combine(ctx.StoreDir, ".staging");
        if (Directory.Exists(staging))
            Directory.Delete(staging, recursive: true);
        ArchiveUtil.ExtractZip(archive, staging);

        var payload = ResolvePayloadRoot(staging);
        var relatives = new List<string>();
        foreach (var file in Directory.EnumerateFiles(payload, "*", SearchOption.AllDirectories))
        {
            ct.ThrowIfCancellationRequested();
            var relative = Path.GetRelativePath(payload, file);
            var target = Path.Combine(root, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target, overwrite: true);
            relatives.Add(relative);
        }

        Directory.Delete(staging, recursive: true);
        var manifestPath = ManifestPath(ctx);
        Directory.CreateDirectory(Path.GetDirectoryName(manifestPath)!);
        File.WriteAllLines(manifestPath, relatives);
        RegisterDlls(ctx);
        return Task.FromResult(ModApplyOutcome.Ok($"Extracted {relatives.Count} file(s) for {ctx.Asset.Id}."));
    }

    public Task<ModApplyOutcome> DisableAsync(ModApplyContext ctx, CancellationToken ct = default)
    {
        var manifest = FindManifestPath(ctx);
        if (manifest is null)
            return Task.FromResult(ModApplyOutcome.NoChange($"{ctx.Asset.Id} is not extracted into the client."));

        var root = TargetRoot(ctx);
        var removed = 0;
        foreach (var relative in ReadManifest(ctx))
        {
            var target = Path.Combine(root, relative);
            if (!File.Exists(target))
                continue;
            File.Delete(target);
            removed++;
            ModFileOps.DeleteEmptyParents(Path.GetDirectoryName(target)!, root);
        }

        File.Delete(manifest);
        foreach (var entry in ctx.Asset.DllsTxtAdd)
            DllsTxt.Remove(ctx.ClientDir, entry);
        return Task.FromResult(ModApplyOutcome.Ok($"Removed {removed} file(s) for {ctx.Asset.Id}."));
    }

    private static void RegisterDlls(ModApplyContext ctx)
    {
        foreach (var entry in ctx.Asset.DllsTxtAdd)
            DllsTxt.Add(ctx.ClientDir, entry);
    }

    /// <summary>Descends through wrapper folders so a zip with one top-level directory still lands flat.</summary>
    private static string ResolvePayloadRoot(string extracted)
    {
        var current = extracted;
        while (!Directory.EnumerateFiles(current).Any())
        {
            var dirs = Directory.EnumerateDirectories(current).ToList();
            if (dirs.Count != 1)
                break;
            current = dirs[0];
        }

        return current;
    }

    private static string TargetRoot(ModApplyContext ctx) =>
        string.IsNullOrWhiteSpace(ctx.Asset.Destination)
            ? ctx.ClientDir
            : ClientPaths.Resolve(ctx.ClientDir, ctx.Asset.Destination);

    private static string ManifestPath(ModApplyContext ctx) =>
        Path.Combine(ctx.ClientDir, ".launcher", "extracted-files", ctx.Asset.Id);

    private static IEnumerable<string> ReadManifest(ModApplyContext ctx)
    {
        var path = FindManifestPath(ctx);
        if (path is null)
            return [];
        return File.ReadAllLines(path).Where(l => !string.IsNullOrWhiteSpace(l));
    }

    private static string? FindManifestPath(ModApplyContext ctx)
    {
        var scoped = ManifestPath(ctx);
        return File.Exists(scoped) ? scoped : null;
    }

    private static string? FindArchive(ModApplyContext ctx)
    {
        if (!Directory.Exists(ctx.StoreDir))
            return null;
        return Directory.EnumerateFiles(ctx.StoreDir, "*.zip", SearchOption.TopDirectoryOnly)
            .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault();
    }
}

/// <summary>VanillaTweaks-style binary patching, always regenerated from the clean executable.</summary>
public sealed class ExecutablePatchHandler : IModHandler
{
    public ModKind Kind => ModKind.ExecutablePatch;

    public bool IsApplied(ModApplyContext ctx) =>
        ctx.CleanExecutableBackup is { } backup && VanillaTweaks.IsPatched(ctx.ClientDir, backup);

    public bool IsInstalled(ModApplyContext ctx) => LocateTool(ctx) is not null;

    public async Task<ModApplyOutcome> EnableAsync(ModApplyContext ctx, CancellationToken ct = default)
    {
        var backup = ctx.CleanExecutableBackup;
        if (backup is null || !File.Exists(backup))
            return ModApplyOutcome.Skip("No clean executable backup; refusing to patch a modified WoW.exe.");

        var tool = LocateTool(ctx);
        var runner = OperatingSystem.IsWindows() ? null : LinuxRunner.ResolveInstalled(ctx.State.Settings, ctx.Paths);
        var result = await VanillaTweaks
            .ApplyAsync(ctx.ClientDir, backup, ctx.State.Settings.VanillaTweaks, tool, ct,
                runner?.Runner, runner?.Prefix ?? ctx.Paths.WinePrefix)
            .ConfigureAwait(false);

        if (result.Applied)
            return ModApplyOutcome.Ok(result.Message);
        return result.ToolMissing ? ModApplyOutcome.Skip(result.Message) : ModApplyOutcome.NoChange(result.Message);
    }

    public Task<ModApplyOutcome> DisableAsync(ModApplyContext ctx, CancellationToken ct = default)
    {
        var backup = ctx.CleanExecutableBackup;
        if (backup is null || !File.Exists(backup))
            return Task.FromResult(ModApplyOutcome.Skip("No clean executable backup to restore."));

        return Task.FromResult(VanillaTweaks.Revert(ctx.ClientDir, backup)
            ? ModApplyOutcome.Ok("Restored the clean WoW.exe.")
            : ModApplyOutcome.NoChange("WoW.exe already matches the clean executable."));
    }

    /// <summary>
    /// Release zips land in <c>mods/</c> and are unpacked on demand so <c>vanilla-tweaks.exe</c>
    /// can be found inside them.
    /// </summary>
    private static string? LocateTool(ModApplyContext ctx) =>
        VanillaTweaks.FindTool(ctx.ClientDir, ctx.StoreDir)
        ?? ModFileOps.ResolveStoreEntry(ctx.StoreDir, VanillaTweaks.ToolName, directory: false);
}

/// <summary>
/// Config.wtf CVar assets. The key/value pairs are carried in <see cref="ManagedAsset.AssetContains"/>
/// as a semicolon separated <c>key=value</c> list, since configuration assets have no download.
/// </summary>
public sealed class ConfigurationHandler : IModHandler
{
    public ModKind Kind => ModKind.Configuration;

    public bool IsApplied(ModApplyContext ctx)
    {
        var wanted = ParseValues(ctx.Asset);
        if (wanted.Count == 0)
            return false;
        var current = ConfigWtf.Read(ConfigPath(ctx));
        return wanted.All(p => current.TryGetValue(p.Key, out var v)
                               && string.Equals(v, p.Value, StringComparison.OrdinalIgnoreCase));
    }

    public bool IsInstalled(ModApplyContext ctx) => true;

    public Task<ModApplyOutcome> EnableAsync(ModApplyContext ctx, CancellationToken ct = default)
    {
        var wanted = ParseValues(ctx.Asset);
        if (wanted.Count == 0)
            return Task.FromResult(ModApplyOutcome.Skip($"{ctx.Asset.Id} declares no configuration values."));

        ConfigWtf.SetValues(ConfigPath(ctx), wanted);
        return Task.FromResult(ModApplyOutcome.Ok($"Applied {wanted.Count} Config.wtf value(s)."));
    }

    public Task<ModApplyOutcome> DisableAsync(ModApplyContext ctx, CancellationToken ct = default)
    {
        var wanted = ParseValues(ctx.Asset);
        if (wanted.Count == 0)
            return Task.FromResult(ModApplyOutcome.NoChange("Nothing to remove."));

        ConfigWtf.Remove(ConfigPath(ctx), wanted.Keys);
        return Task.FromResult(ModApplyOutcome.Ok($"Removed {wanted.Count} Config.wtf value(s)."));
    }

    private static string ConfigPath(ModApplyContext ctx) =>
        string.IsNullOrWhiteSpace(ctx.Asset.Destination)
            ? ClientPaths.Resolve(ctx.ClientDir, "WTF", "Config.wtf")
            : ClientPaths.Resolve(ctx.ClientDir, ctx.Asset.Destination);

    public static Dictionary<string, string> ParseValues(ManagedAsset asset)
    {
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(asset.AssetContains))
            return values;

        foreach (var part in asset.AssetContains.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var eq = part.IndexOf('=');
            if (eq <= 0)
                continue;
            values[part[..eq].Trim()] = part[(eq + 1)..].Trim().Trim('"');
        }

        return values;
    }
}

/// <summary>
/// DXVK's <c>d3d9.dll</c>, placed beside <c>WoW.exe</c>. On Linux, Play sets
/// <c>WINEDLLOVERRIDES=d3d9=n,b</c> when that file is present. Release archives nest the
/// 32-bit DLL under <c>x32/</c>.
/// </summary>
public sealed class DxvkHandler : IModHandler
{
    public ModKind Kind => ModKind.Dxvk;

    public bool IsApplied(ModApplyContext ctx) => File.Exists(ctx.LivePath);

    public bool IsInstalled(ModApplyContext ctx) => IsApplied(ctx) || FindSource(ctx) is not null;

    public Task<ModApplyOutcome> EnableAsync(ModApplyContext ctx, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        if (IsApplied(ctx) && (string.IsNullOrWhiteSpace(ctx.Asset.Sha256) || Checksums.VerifyFile(ctx.LivePath, ctx.Asset.Sha256)))
            return Task.FromResult(ModApplyOutcome.NoChange("d3d9.dll is already installed."));

        var source = FindSource(ctx);
        if (source is null)
            return Task.FromResult(ModApplyOutcome.Skip("d3d9.dll was not found. Download DXVK or place x32/d3d9.dll in mods/dxvk."));

        Directory.CreateDirectory(Path.GetDirectoryName(ctx.LivePath)!);
        File.Copy(source, ctx.LivePath, overwrite: true);
        return Task.FromResult(ModApplyOutcome.Ok("Installed d3d9.dll beside the game executable."));
    }

    public Task<ModApplyOutcome> DisableAsync(ModApplyContext ctx, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        if (!File.Exists(ctx.LivePath))
            return Task.FromResult(ModApplyOutcome.NoChange("d3d9.dll is not installed."));

        Directory.CreateDirectory(ctx.StoreDir);
        var parked = Path.Combine(ctx.StoreDir, "d3d9.dll");
        if (File.Exists(parked))
            File.Delete(parked);
        File.Move(ctx.LivePath, parked);
        return Task.FromResult(ModApplyOutcome.Ok("Removed d3d9.dll."));
    }

    private static string? FindSource(ModApplyContext ctx)
    {
        var loose = Path.Combine(ctx.StoreDir, "d3d9.dll");
        if (File.Exists(loose))
            return loose;
        if (!Directory.Exists(ctx.StoreDir))
            return null;

        foreach (var archive in Directory.EnumerateFiles(ctx.StoreDir))
        {
            var name = Path.GetFileName(archive);
            if (!name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)
                && !name.EndsWith(".tar.gz", StringComparison.OrdinalIgnoreCase)
                && !name.EndsWith(".tgz", StringComparison.OrdinalIgnoreCase))
                continue;

            var extract = Path.Combine(ctx.StoreDir, ".dxvk-extract");
            if (Directory.Exists(extract))
                Directory.Delete(extract, recursive: true);
            Directory.CreateDirectory(extract);
            try
            {
                if (name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
                    ArchiveUtil.ExtractZip(archive, extract);
                else
                    ArchiveUtil.ExtractTarGz(archive, extract);
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException)
            {
                continue;
            }

            var dll = PickDll(extract);
            if (dll is null)
                continue;
            var staged = Path.Combine(ctx.StoreDir, "d3d9.dll");
            File.Copy(dll, staged, overwrite: true);
            return staged;
        }

        return Directory.EnumerateFiles(ctx.StoreDir, "d3d9.dll", SearchOption.AllDirectories)
            .FirstOrDefault(path => path.Contains($"{Path.DirectorySeparatorChar}x32{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase))
            ?? Directory.EnumerateFiles(ctx.StoreDir, "d3d9.dll", SearchOption.AllDirectories).FirstOrDefault();
    }

    private static string? PickDll(string root)
    {
        var dlls = Directory.EnumerateFiles(root, "d3d9.dll", SearchOption.AllDirectories).ToList();
        return dlls.FirstOrDefault(path =>
                   path.Contains($"{Path.DirectorySeparatorChar}x32{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase)
                   || path.Contains($"{Path.DirectorySeparatorChar}x86{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase))
               ?? dlls.FirstOrDefault();
    }
}

/// <summary>Kinds the launcher recognises in the manifest schema but does not install yet.</summary>
public sealed class UnsupportedHandler : IModHandler
{
    public static readonly UnsupportedHandler Instance = new();

    public ModKind Kind => ModKind.Glue;

    public bool IsApplied(ModApplyContext ctx) => false;
    public bool IsInstalled(ModApplyContext ctx) => false;

    public Task<ModApplyOutcome> EnableAsync(ModApplyContext ctx, CancellationToken ct = default) =>
        Task.FromResult(ModApplyOutcome.Skip($"Asset kind {ctx.Asset.Kind} is not installable by this launcher."));

    public Task<ModApplyOutcome> DisableAsync(ModApplyContext ctx, CancellationToken ct = default) =>
        Task.FromResult(ModApplyOutcome.Skip($"Asset kind {ctx.Asset.Kind} is not installable by this launcher."));
}
