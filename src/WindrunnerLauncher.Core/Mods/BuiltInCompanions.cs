using WindrunnerLauncher.Core.Models;

namespace WindrunnerLauncher.Core.Mods;

/// <summary>
/// Patch pairs that ship outside the signed client manifest.
///
/// HD Character Models (<c>patch-A.mpq</c>) needs a small overlay that restores files the other
/// HD patches overwrite. The overlay is published as its own tagged release in the Windrunner
/// server repository, so it can be corrected without republishing the signed manifest. It is
/// modelled as a normal managed asset so the existing download, store and toggle machinery can
/// put <c>patch-X.mpq</c> into <c>Data</c> and take it out again.
/// </summary>
internal static class BuiltInCompanions
{
    public const string HdPatchAId = "hd_patch_a";
    public const string HdPatchACompatId = "patch-a-compat";
    public const string Repository = "WindrunnerWoW/windrunner-wow";
    public const string ReleaseTagName = "patch-a-compat";
    public const string CompatDestination = "Data/patch-X.mpq";

    /// <summary>
    /// Adds the HD Patch A overlay to a loaded manifest. A manifest that already carries the
    /// asset (or that uses <c>patch-X.mpq</c> for something else) is left alone apart from the
    /// companion link on HD Patch A.
    /// </summary>
    public static void Apply(ClientManifest manifest)
    {
        var owner = manifest.Assets.FirstOrDefault(asset =>
            asset.Id.Equals(HdPatchAId, StringComparison.OrdinalIgnoreCase));
        if (owner is null)
            return;

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
                DisplayName = "HD Character Models compatibility patch",
                Kind = ModKind.Mpq,
                Destination = CompatDestination,
                Optional = true,
                Category = "Graphics",
                Description = "Replaces the files the other HD patches overwrite so HD Character Models keep working. Downloaded and enabled together with HD Character Models.",
                GitHubRepo = Repository,
                ReleaseTag = ReleaseTagName
            };
            manifest.Assets.Add(companion);
        }

        if (!owner.Companions.Any(id => id.Equals(companion.Id, StringComparison.OrdinalIgnoreCase)))
            owner.Companions.Add(companion.Id);
    }
}
