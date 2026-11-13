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

        var tocs = Tocs(dir);
        if (tocs.Count > 0)
        {
            found.Add(new AddonFolder(ResolveFolderName(dir, isRoot, tocs), dir));
            return;
        }

        foreach (var child in Directory.EnumerateDirectories(dir))
            Collect(child, isRoot: false, found, depth + 1);
    }

    /// <summary>
    /// The folder name an addon has to be installed under, read from the <c>.toc</c> files of an
    /// already extracted folder, or <c>null</c> when the folder holds no addon.
    /// </summary>
    public static string? ResolveFolderName(string addonDir)
    {
        if (!Directory.Exists(addonDir))
            return null;
        var tocs = Tocs(addonDir);
        return tocs.Count == 0 ? null : ResolveFolderName(addonDir, isRoot: false, tocs);
    }

    /// <summary>
    /// World of Warcraft only loads a folder whose name matches the base name of its <c>.toc</c>
    /// file, so the name is read from the <c>.toc</c> rather than from the directory. Downloads
    /// arrive wrapped in a folder named after the archive instead ("AtlasLoot-main",
    /// "repo-master"), which the game ignores.
    /// </summary>
    private static string ResolveFolderName(string dir, bool isRoot, List<string> tocs)
    {
        var names = tocs
            .Select(toc => Path.GetFileNameWithoutExtension(toc))
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var folder = Path.GetFileName(dir);
        if (names.Count == 0)
            return folder;

        // A folder that already carries the right name keeps its exact casing.
        if (!isRoot && names.Contains(folder, StringComparer.OrdinalIgnoreCase))
            return folder;

        if (names.Count == 1)
            return names[0];

        // Multi-flavour addons ship one .toc per client version (AtlasLoot.toc,
        // AtlasLoot_Vanilla.toc, AtlasLoot-tbc.toc, ...). The prefix those names share is the
        // folder name.
        var shared = SharedPrefix(names);
        if (shared.Length > 0)
            return shared;

        return names.OrderBy(name => name.Length).ThenBy(name => name, StringComparer.OrdinalIgnoreCase).First();
    }

    /// <summary>Longest prefix shared by every name, cut back to a separator boundary.</summary>
    private static string SharedPrefix(List<string> names)
    {
        var prefix = names[0];
        foreach (var name in names.Skip(1))
        {
            var length = 0;
            while (length < prefix.Length && length < name.Length
                   && char.ToLowerInvariant(prefix[length]) == char.ToLowerInvariant(name[length]))
                length++;
            prefix = prefix[..length];
            if (prefix.Length == 0)
                return "";
        }

        // "AtlasLoot_Vanilla" and "AtlasLoot_Wrath" share "AtlasLoot_"; the separator is not part of the name.
        return prefix.TrimEnd('_', '-', '.', ' ');
    }

    private static List<string> Tocs(string dir) =>
        Directory.EnumerateFiles(dir, "*.toc", SearchOption.TopDirectoryOnly).ToList();
}
