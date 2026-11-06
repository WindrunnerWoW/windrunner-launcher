using WindrunnerLauncher.Core.Hearthstone;

namespace WindrunnerLauncher.Core.Tests;

/// <summary>The addon's texture lists, generated from the module's manifests and the built texture folder.</summary>
public class HearthstoneLuaListsTests
{
    private const string ModuleRoot = @"C:\TurtleDev\01_source\server\modules\mod-hearthstone";
    private const string ClientTextures = @"C:\Windrunner\client\Interface\Hearthstone\Textures";

    [Fact]
    public void Lists_every_built_texture_the_manifests_know()
    {
        // uses the developer machine's module checkout and an installed client; nothing to check elsewhere
        if (!Directory.Exists(ModuleRoot) || !Directory.Exists(ClientTextures))
            return;

        var warnings = new List<string>();
        var files = HearthstoneLuaLists.Generate(new HearthstoneArtInputs(ModuleRoot), ClientTextures, warnings);

        Assert.Contains("generated = true", files["CardTextures.lua"]);
        Assert.DoesNotContain("count = 0,\n    order", files["CardTextures.lua"]);
        Assert.Contains("HS_MinionTextures", files["CardTextures.lua"]);
        Assert.Contains("HS_HeroTextures", files["CardTextures.lua"]);
        Assert.Contains("generated = true", files["BoardTextures.lua"]);
        Assert.Contains("Interface\\\\Hearthstone\\\\Textures\\\\Board\\\\", files["BoardTextures.lua"]);
        Assert.Contains("generated = true", files["CollectionTextures.lua"]);
        Assert.Contains("HS_BgTextures", files["Battlegrounds/BgTextures.lua"]);
        Assert.All(files.Values, v => Assert.DoesNotContain("Placeholder", v));
        Assert.All(files.Values, v => Assert.DoesNotContain("\r", v));
    }

    [Fact]
    public void Writes_files_below_the_addon_folder_without_bom()
    {
        var dir = Path.Combine(Path.GetTempPath(), "hs-lua-" + Guid.NewGuid().ToString("N"));
        try
        {
            HearthstoneLuaLists.Write(dir, new Dictionary<string, string> { ["Battlegrounds/BgTextures.lua"] = "x = 1\n" });
            var bytes = File.ReadAllBytes(Path.Combine(dir, "Battlegrounds", "BgTextures.lua"));
            Assert.Equal("x = 1\n", System.Text.Encoding.UTF8.GetString(bytes));
        }
        finally
        {
            if (Directory.Exists(dir))
                Directory.Delete(dir, recursive: true);
        }
    }
}
