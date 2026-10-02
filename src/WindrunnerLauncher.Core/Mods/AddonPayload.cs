namespace WindrunnerLauncher.Core.Mods;

/// <summary>Unpacks a downloaded addon zip and locates its Interface/AddOns folder(s) inside.</summary>
public static class AddonPayload
{
    public sealed record AddonFolder(string Name, string SourcePath);

    /// <summary>Recognises numbered byte segments of one ZIP, such as data.zip.001.</summary>
    public static bool TryGetSplitZipPart(string name, out string archiveName, out int partNumber)
    {
        archiveName = name;
        partNumber = 0;
        var dot = name.LastIndexOf('.');
        if (dot < 0 || !name[..dot].EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
            return false;
        var suffix = name.AsSpan(dot + 1);
        if (suffix.Length < 3 || suffix.ContainsAnyExceptInRange('0', '9') || !int.TryParse(suffix, out partNumber))
            return false;
        archiveName = name[..dot];
        return true;
    }

    /// <summary>
    /// Extracts <paramref name="zipPath"/> into <paramref name="stagingDir"/> (recreated if it
    /// already exists) and walks down through any wrapper folders (a release zip, or a full
    /// <c>Interface/AddOns/...</c> path) until it finds folders that carry a <c>.toc</c> file.
    /// </summary>
    public static List<AddonFolder> ExtractAddonFolders(string zipPath, string stagingDir)
    {
        ExtractArchive(zipPath, stagingDir);
        return FindAddonFolders(stagingDir);
    }

    /// <summary>Extracts an archive into a recreated directory without requiring a .toc yet.</summary>
    public static void ExtractArchive(string zipPath, string stagingDir)
    {
        if (Directory.Exists(stagingDir))
            Directory.Delete(stagingDir, recursive: true);
        ArchiveUtil.ExtractZip(zipPath, stagingDir);
    }

    /// <summary>Finds addon folders in an already extracted archive tree.</summary>
    public static List<AddonFolder> FindAddonFolders(string stagingDir)
    {
        var found = new List<AddonFolder>();
        Collect(stagingDir, isRoot: true, found, depth: 0);
        return found;
    }

    private static void Collect(string dir, bool isRoot, List<AddonFolder> found, int depth)
    {
        if (depth > 6)
            return;

        var toc = FirstToc(dir);
        if (toc is not null)
        {
            // A zip that drops its files straight at the root, with no wrapping folder, has no
            // real folder name to read - fall back to the .toc file's own base name.
            var name = isRoot ? Path.GetFileNameWithoutExtension(toc) : Path.GetFileName(dir);
            found.Add(new AddonFolder(name, dir));
            return;
        }

        foreach (var child in Directory.EnumerateDirectories(dir))
            Collect(child, isRoot: false, found, depth + 1);
    }

    private static string? FirstToc(string dir) =>
        Directory.EnumerateFiles(dir, "*.toc", SearchOption.TopDirectoryOnly).FirstOrDefault();
}
