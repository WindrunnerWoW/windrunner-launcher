using WindrunnerLauncher.Core.Mods;

namespace WindrunnerLauncher.Core.Tests;

public sealed class AddonPayloadTests
{
    [Theory]
    [InlineData("AtlasLoot-main", new[] { "AtlasLoot.toc" }, "AtlasLoot")]
    [InlineData("aux-addon-vanilla-master", new[] { "aux-addon.toc" }, "aux-addon")]
    [InlineData("pfUI-master", new[] { "pfUI.toc", "pfUI-tbc.toc" }, "pfUI")]
    [InlineData("ShaguDPS-master", new[] { "ShaguDPS.toc", "ShaguDPS-tbc.toc" }, "ShaguDPS")]
    [InlineData("AI_VoiceOver", new[] { "AI_VoiceOver.toc", "AI_VoiceOver_1.12.toc" }, "AI_VoiceOver")]
    [InlineData("AtlasLoot-main", new[] { "AtlasLoot_Mainline.toc", "AtlasLoot_Vanilla.toc" }, "AtlasLoot")]
    [InlineData("AtlasLoot", new[] { "AtlasLoot.toc" }, "AtlasLoot")]
    public void ResolveFolderName_ReadsTheNameFromTheToc(string folder, string[] tocs, string expected)
    {
        using var tmp = new TempDir("addon-name");
        var dir = tmp.Dir(folder);
        foreach (var toc in tocs)
            File.WriteAllText(Path.Combine(dir, toc), "## Interface: 11200\n");

        Assert.Equal(expected, AddonPayload.ResolveFolderName(dir));
    }

    [Fact]
    public void ResolveFolderName_ReturnsNullWithoutToc()
    {
        using var tmp = new TempDir("addon-name-none");

        Assert.Null(AddonPayload.ResolveFolderName(tmp.Dir("empty")));
        Assert.Null(AddonPayload.ResolveFolderName(tmp.Combine("missing")));
    }

    [Fact]
    public void FindAddonFolders_NamesFoldersAfterTheirTocAndStopsAtTheAddon()
    {
        using var tmp = new TempDir("addon-find");
        var staging = tmp.Dir("staging");
        var addon = Directory.CreateDirectory(Path.Combine(staging, "AtlasLoot-main")).FullName;
        File.WriteAllText(Path.Combine(addon, "AtlasLoot.toc"), "## Interface: 11200\n");
        var lib = Directory.CreateDirectory(Path.Combine(addon, "Libs", "LibStub")).FullName;
        File.WriteAllText(Path.Combine(lib, "LibStub.toc"), "");

        var found = Assert.Single(AddonPayload.FindAddonFolders(staging));

        Assert.Equal("AtlasLoot", found.Name);
        Assert.Equal(addon, found.SourcePath);
    }

    [Fact]
    public void FindAddonFolders_NamesRootLevelFilesAfterTheirToc()
    {
        using var tmp = new TempDir("addon-find-root");
        var staging = tmp.Dir("staging");
        File.WriteAllText(Path.Combine(staging, "Mail.toc"), "## Interface: 11200\n");

        var found = Assert.Single(AddonPayload.FindAddonFolders(staging));

        Assert.Equal("Mail", found.Name);
        Assert.Equal(staging, found.SourcePath);
    }
}
