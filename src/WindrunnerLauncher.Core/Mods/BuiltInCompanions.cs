using WindrunnerLauncher.Core.Models;

namespace WindrunnerLauncher.Core.Mods;

/// <summary>
/// Patch pairs that ship outside the signed client manifest.
///
/// HD Character Models, HD patch G and HD patch C (<c>patch-A/G/C.mpq</c>) share one small overlay
/// that restores files the other HD patches overwrite. It stays enabled while any of them is on, and the three patches switch each other on and off. The overlay is published as its own tagged release in the Windrunner
/// server repository, so it can be corrected without republishing the signed manifest. It is
/// modelled as a normal managed asset so the existing download, store and toggle machinery can
/// put <c>patch-X.mpq</c> into <c>Data</c> and take it out again.
/// </summary>
internal static class BuiltInCompanions
{
    public const string HdPatchAId = "hd_patch_a";
    public const string HdPatchGId = "hd_patch_g";
    public const string HdPatchCId = "hd_patch_c";

    /// <summary>Patches that break without the overlay.</summary>
    public static readonly string[] HdPatchOwnerIds = [HdPatchAId, HdPatchGId, HdPatchCId];

    public const string HdPatchACompatId = "patch-a-compat";
    public const string Repository = "WindrunnerWoW/windrunner-wow";
    public const string ReleaseTagName = "patch-a-compat";
    public const string CompatDestination = "Data/patch-X.mpq";

    /// <summary>
    /// Adds the overlay to a loaded manifest and links it to every owner the manifest has. A
    /// manifest that already carries the asset is kept; one that uses <c>patch-X.mpq</c> for
    /// something else is left alone.
    /// </summary>
    public static void Apply(ClientManifest manifest)
    {
        var owners = manifest.Assets
            .Where(asset => HdPatchOwnerIds.Contains(asset.Id, StringComparer.OrdinalIgnoreCase))
            .ToList();
        if (owners.Count == 0)
            return;

        // Their DBC changes only work together, so enabling one enables the others, and
        // disabling one disables the others (the same pairing as HD Buildings/Doodads/Environment).
        foreach (var owner in owners)
        {
            foreach (var other in owners)
            {
                if (!ReferenceEquals(owner, other)
                    && !owner.Dependencies.Contains(other.Id, StringComparer.OrdinalIgnoreCase))
                    owner.Dependencies.Add(other.Id);
            }
        }

        var leaf = ModPaths.LeafName(CompatDestination);
        var destinationTaken = manifest.Assets.Any(asset =>
            !asset.Id.Equals(HdPatchACompatId, StringComparison.OrdinalIgnoreCase)
            && ModPaths.LeafName(asset.Destination).Equals(leaf, StringComparison.OrdinalIgnoreCase));
        if (destinationTaken)
            return;

        var companion = manifest.Assets.FirstOrDefault(asset =>
            asset.Id.Equals(HdPatchACompatId, StringComparison.OrdinalIgnoreCase));
        if (companion is null)
        {
            companion = new ManagedAsset
            {
                Id = HdPatchACompatId,
                DisplayName = "HD compatibility patch",
                Kind = ModKind.Mpq,
                Destination = CompatDestination,
                Optional = true,
                Category = "Graphics",
                Description = "Replaces the files the other HD patches overwrite so HD Character Models and the related HD patches keep working. Downloaded, updated and enabled together with them.",
                GitHubRepo = Repository,
                ReleaseTag = ReleaseTagName
            };
            manifest.Assets.Add(companion);
        }

        foreach (var owner in owners)
        {
            if (!owner.Companions.Any(id => id.Equals(companion.Id, StringComparison.OrdinalIgnoreCase)))
                owner.Companions.Add(companion.Id);
        }
    }
}
