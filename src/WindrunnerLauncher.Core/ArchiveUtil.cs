using System.Formats.Tar;
using System.IO.Compression;

namespace WindrunnerLauncher.Core;

public static class ArchiveUtil
{
    public static void ExtractZip(string zipPath, string destDir)
    {
        Directory.CreateDirectory(destDir);
        using var zip = ZipFile.OpenRead(zipPath);
        foreach (var entry in zip.Entries)
        {
            if (string.IsNullOrEmpty(entry.Name) && entry.FullName.EndsWith('/'))
            {
                var dir = Path.GetFullPath(Path.Combine(destDir, entry.FullName));
                EnsureInside(destDir, dir);
                Directory.CreateDirectory(dir);
                continue;
            }

            var target = Path.GetFullPath(Path.Combine(destDir, entry.FullName));
            EnsureInside(destDir, target);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            entry.ExtractToFile(target, overwrite: true);
        }
    }

    public static bool LooksLikeGzip(string path)
    {
        try
        {
            using var stream = File.OpenRead(path);
            Span<byte> magic = stackalloc byte[2];
            return stream.Read(magic) == 2 && magic[0] == 0x1F && magic[1] == 0x8B;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>
    /// Unpacks a <c>.tar.gz</c> archive. Symbolic links are recreated, and Unix mode bits from the
    /// tar header are applied when the host supports them. The MariaDB bintar depends on both.
    /// </summary>
    public static void ExtractTarGz(string archivePath, string destDir)
    {
        using var file = File.OpenRead(archivePath);
        using var gzip = new GZipStream(file, CompressionMode.Decompress);
        ExtractTarStream(gzip, destDir);
    }

    /// <summary>A <c>.tar</c> or <c>.tar.gz</c>, told apart by the gzip header.</summary>
    public static void ExtractTar(string archivePath, string destDir)
    {
        if (LooksLikeGzip(archivePath))
        {
            ExtractTarGz(archivePath, destDir);
            return;
        }

        using var file = File.OpenRead(archivePath);
        ExtractTarStream(file, destDir);
    }

    private static void ExtractTarStream(Stream stream, string destDir)
    {
        Directory.CreateDirectory(destDir);
        using var reader = new TarReader(stream);
        while (reader.GetNextEntry() is { } entry)
        {
            if (entry.EntryType is TarEntryType.GlobalExtendedAttributes or TarEntryType.ExtendedAttributes)
                continue;

            var relative = entry.Name.Replace('\\', '/').TrimStart('/');
            if (relative.Length == 0)
                continue;
            var target = Path.GetFullPath(Path.Combine(destDir, relative.Replace('/', Path.DirectorySeparatorChar)));
            EnsureInside(destDir, target);
            EnsureNoLinkedParent(destDir, target);

            switch (entry.EntryType)
            {
                case TarEntryType.Directory:
                case TarEntryType.DirectoryList:
                    Directory.CreateDirectory(target);
                    break;
                case TarEntryType.SymbolicLink:
                    Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                    ReplaceWithLink(destDir, target, entry.LinkName ?? "");
                    break;
                case TarEntryType.HardLink:
                    Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                    CopyHardLink(destDir, target, entry.LinkName ?? "");
                    break;
                case TarEntryType.RegularFile:
                case TarEntryType.V7RegularFile:
                case TarEntryType.ContiguousFile:
                    Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                    RemoveExisting(target);
                    entry.ExtractToFile(target, overwrite: true);
                    ApplyUnixMode(target, entry.Mode);
                    break;
            }
        }
    }

    private static void ReplaceWithLink(string destDir, string target, string linkTarget)
    {
        if (!IsSafeLinkTarget(destDir, target, linkTarget))
            throw new InvalidDataException($"Tar link escapes destination: {linkTarget}");

        RemoveExisting(target);
        File.CreateSymbolicLink(target, linkTarget);
    }

    /// <summary>
    /// Hard-link names in a tar are paths of an earlier member, usually from the archive root.
    /// Copy that file. A symlink with an archive-root target would point at the wrong place.
    /// </summary>
    private static void CopyHardLink(string destDir, string target, string linkName)
    {
        var source = ExistingHardLinkSource(destDir, target, linkName)
                     ?? throw new InvalidDataException($"Tar hard link has no target inside the archive: {linkName}");
        RemoveExisting(target);
        File.Copy(source, target, overwrite: true);
        if (!OperatingSystem.IsWindows())
            ApplyUnixMode(target, File.GetUnixFileMode(source));
    }

    private static string? ExistingHardLinkSource(string destDir, string linkPath, string linkName)
    {
        if (string.IsNullOrWhiteSpace(linkName))
            return null;

        var normalized = linkName.Replace('\\', '/').TrimStart('/');
        foreach (var start in new[] { Path.GetFullPath(destDir), Path.GetDirectoryName(linkPath)! })
        {
            string candidate;
            try
            {
                // Follow links already on disk, so a chain of in-archive links cannot copy a file
                // from outside the destination.
                candidate = ResolveInside(destDir, start, normalized);
            }
            catch (InvalidDataException)
            {
                continue;
            }

            if (File.Exists(candidate))
                return candidate;
        }

        return null;
    }

    private static bool IsSafeLinkTarget(string destDir, string linkPath, string linkTarget)
    {
        if (string.IsNullOrWhiteSpace(linkTarget) || linkTarget.Contains('\0'))
            return false;
        if (linkTarget.StartsWith('/') || linkTarget.StartsWith('\\'))
            return false;
        if (linkTarget.Length >= 2 && linkTarget[1] == ':')
            return false;

        try
        {
            ResolveInside(destDir, Path.GetDirectoryName(linkPath)!, linkTarget);
            return true;
        }
        catch (InvalidDataException)
        {
            return false;
        }
    }

    /// <summary>
    /// Walks <paramref name="relative"/> from <paramref name="start"/> one segment at a time and
    /// follows every symlink already on disk. A text-only check is not enough: after <c>a -&gt; .</c>,
    /// the path <c>a/..</c> looks like the destination but is really its parent.
    /// </summary>
    private static string ResolveInside(string root, string start, string relative, int depth = 0)
    {
        if (depth > 32)
            throw new InvalidDataException($"Tar link chain is too deep: {relative}");

        var current = Path.GetFullPath(start);
        EnsureInside(root, current);
        foreach (var segment in relative.Split('/', '\\', StringSplitOptions.RemoveEmptyEntries))
        {
            if (segment == ".")
                continue;
            if (segment == "..")
            {
                current = Path.GetDirectoryName(current)
                          ?? throw new InvalidDataException($"Tar link escapes destination: {relative}");
                EnsureInside(root, current);
                continue;
            }

            var next = Path.Combine(current, segment);
            var link = new FileInfo(next).LinkTarget;
            if (link is not null)
            {
                if (Path.IsPathRooted(link))
                    throw new InvalidDataException($"Tar link escapes destination: {link}");
                next = ResolveInside(root, current, link, depth + 1);
            }

            EnsureInside(root, next);
            current = next;
        }

        return current;
    }

    /// <summary>
    /// Refuses to write through a folder that is a symlink. Otherwise an archive could first add
    /// a link to a folder and then write files into wherever that link points.
    /// </summary>
    private static void EnsureNoLinkedParent(string root, string target)
    {
        var rootFull = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar);
        for (var dir = Path.GetDirectoryName(target);
             dir is not null && dir.Length > rootFull.Length;
             dir = Path.GetDirectoryName(dir))
        {
            if (new FileInfo(dir).LinkTarget is not null)
                throw new InvalidDataException($"Tar entry would be written through a symlink: {target}");
        }
    }

    private static void RemoveExisting(string target)
    {
        try
        {
            File.Delete(target);
            return;
        }
        catch (FileNotFoundException)
        {
            return;
        }
        catch (DirectoryNotFoundException)
        {
            return;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A directory, or a symlink to one. Fall through and remove that.
        }

        if (Directory.Exists(target))
            Directory.Delete(target, recursive: true);
    }

    private static void ApplyUnixMode(string path, UnixFileMode mode)
    {
        if (OperatingSystem.IsWindows() || mode == UnixFileMode.None)
            return;
        try
        {
            File.SetUnixFileMode(path, mode);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The bytes are already on disk. A later chmod of bin/ covers the daemons.
        }
    }

    public static void ZipDirectory(string sourceDir, string zipPath)
    {
        if (File.Exists(zipPath))
            File.Delete(zipPath);
        Directory.CreateDirectory(Path.GetDirectoryName(zipPath)!);
        ZipFile.CreateFromDirectory(sourceDir, zipPath, CompressionLevel.Fastest, includeBaseDirectory: false);
    }

    public static string? FindNestedRoot(string extracted, params string[] requiredFiles)
    {
        if (requiredFiles.All(f => File.Exists(Path.Combine(extracted, f))))
            return extracted;
        foreach (var dir in Directory.EnumerateDirectories(extracted))
        {
            if (requiredFiles.All(f => File.Exists(Path.Combine(dir, f))))
                return dir;
            foreach (var nested in Directory.EnumerateDirectories(dir))
            {
                if (requiredFiles.All(f => File.Exists(Path.Combine(nested, f))))
                    return nested;
            }
        }

        return null;
    }

    private static void EnsureInside(string root, string target)
    {
        var prefix = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!target.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(Path.GetFullPath(root), target, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException($"Zip entry escapes destination: {target}");
    }
}
