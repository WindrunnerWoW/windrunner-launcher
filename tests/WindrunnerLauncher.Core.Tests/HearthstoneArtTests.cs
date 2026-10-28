using System.Security.Cryptography;
using System.Text;
using WindrunnerLauncher.Core.Hearthstone;
using WindrunnerLauncher.Core.Hearthstone.Art;
using WindrunnerLauncher.Core.Hearthstone.Imaging;

namespace WindrunnerLauncher.Core.Tests;

/// <summary>
/// The Hearthstone art builder must produce the same bytes as the module's Pillow pipeline. The expected hashes
/// were computed with Pillow 12.3 (and tools/art/convert_blp.py for the BLP) from the same synthetic pattern.
/// </summary>
public class HearthstoneArtTests
{
    private static PilImage Pattern(int w, int h)
    {
        var im = new PilImage(PilMode.RGBA, w, h);
        for (var y = 0; y < h; y++)
        {
            for (var x = 0; x < w; x++)
            {
                var a = (x * 7 + y * 3) % 23 == 0 ? 0 : (x + y) % 5 != 0 ? 255 : x * y % 256;
                im.SetRgba(x, y, (x * 31 + y * 17) % 256, (x * 13 + y * 29 + 7) % 256, (x * x + y) % 256, a);
            }
        }

        return im;
    }

    private static string Sha(byte[] data) => Convert.ToHexStringLower(SHA256.HashData(data));

    private static byte[] Rgb(PilImage im) =>
        im.Data.Where((_, i) => i % 4 != 3).ToArray();

    [Fact]
    public void Resampling_matches_pillow()
    {
        var im = Pattern(40, 30);
        Assert.Equal("cc6483b517bd3e5481044c47d881f80a25d7a26d400825c5eff726dd50d19f65",
            Sha(PilOps.Resize(im, 17, 23, Resampling.Lanczos, (3.25, 2.5, 31.75, 27.0)).Data));
        Assert.Equal("098f4c19b15f33dac7c9f12b8e7388d017df5f2389459d0cfef312c8637d8adc",
            Sha(PilOps.Resize(im, 20, 15, Resampling.Box).Data));
        Assert.Equal("47ba25cb95160e8d54e4e7b80532cadbc52b882f9d6ec26dbdfdc3e9ccd50cc2",
            Sha(PilOps.Resize(im, 53, 11, Resampling.Bicubic).Data));
    }

    [Fact]
    public void Filters_match_pillow()
    {
        var im = Pattern(40, 30);
        var l = im.Convert(PilMode.L);
        Assert.Equal("e2fcd72b36144cb5637e2bb35515adf26f48537fac14f30864b8f542c66d7c53", Sha(PilOps.MaxFilter(l, 5).Data));
        Assert.Equal("3dde97415948117ced6223a979df922f753a82c6d471ad4bb1406c226df3975b",
            Sha(Rgb(PilOps.MinFilter(im.Convert(PilMode.RGB), 3))));
        Assert.Equal("5161f9b3de62327b23818a328ef46298447f7b874eb628d6b3cae9acd00686f6", Sha(PilOps.GaussianBlur(l, 0.7f).Data));
        Assert.Equal("34b9f939aebceb801f48d497d77b9e8fb8843d5c33ffdc5203c81ffc3226d6d7", Sha(PilOps.GaussianBlur(im, 4).Data));
    }

    [Fact]
    public void Rotation_and_compositing_match_pillow()
    {
        var im = Pattern(40, 30);
        Assert.Equal("634aadeee6e711373a637ffcbdaaa73b8259bf82cacdeecb5bf948ce475b5b85",
            Sha(PilOps.Rotate(im, -7.5, Resampling.Bicubic, (12.25, 9.5), (0.3, -0.6)).Data));
        Assert.Equal("07d63e7025f9a220b83e5c31b928339d65686d8dbfd75e7cc3af2a6281d8c629",
            Sha(PilOps.Rotate(im.Convert(PilMode.L), 11.0, Resampling.Bilinear, (20.5, 3.5)).Data));

        var bse = Pattern(24, 20);
        bse.AlphaComposite(Pattern(9, 7).FlipTopBottom(), 4, 5);
        Assert.Equal("2b66dc12e3f5ef1c4046f3408890847fb4b717fc8b8de7f9c0dda56cc677cdf4", Sha(bse.Data));
    }

    [Fact]
    public void Blp_matches_the_module_encoder()
    {
        var (blp, levels) = BlpWriter.Build(Pattern(64, 32));
        Assert.Equal(7, levels);
        Assert.Equal(3940, blp.Length);
        Assert.Equal("BLP2", Encoding.ASCII.GetString(blp, 0, 4));
        Assert.Equal("4f6de58219da098336a8205194b976b9b178c3df7f1b69a6d5d66334ff2fe982", Sha(blp));
    }

    [Fact]
    public void Decoded_png_round_trips()
    {
        using var dir = new TempDir("hs");
        var path = dir.Combine("p.png");
        var im = Pattern(20, 10);
        ImageCodec.SavePng(im, path);
        var back = ImageCodec.LoadRgba(path);
        Assert.Equal(im.Data, back.Data);
        Assert.Equal((20, 10), ImageCodec.PngSize(File.ReadAllBytes(path)));
    }

    [Fact]
    public void Git_blob_id_matches_git()
    {
        // git hash-object of "hello\n"
        Assert.Equal("ce013625030ba8dba906f756967f9e9ca394464a", HearthstoneModuleSource.GitBlobSha("hello\n"u8.ToArray()));
    }

    [Fact]
    public void Snapshot_fingerprint_ignores_order_and_follows_content()
    {
        ModuleSnapshot Snap(params (string Path, string Sha)[] files) => new()
        {
            Commit = "c",
            Inputs = files.Select(f => new ModuleFile(f.Path, f.Sha)).ToList(),
            Textures = []
        };

        var a = Snap(("a.json", "1"), ("b.json", "2")).Fingerprint(1);
        Assert.Equal(a, Snap(("b.json", "2"), ("a.json", "1")).Fingerprint(1));
        Assert.NotEqual(a, Snap(("a.json", "1"), ("b.json", "3")).Fingerprint(1));
        Assert.NotEqual(a, Snap(("a.json", "1"), ("b.json", "2")).Fingerprint(2));
    }

    [Fact]
    public void Battlegrounds_table_rows_are_parsed()
    {
        const string inc = """
            // Row: id, dbfId, tier, attack, health, tribes, keywords, golden id, ...
                    {"BG19_010", 70790, 2, 3, 2, kTribeBeast, 0, "BG19_010_G", 70801, 6, 4, 0, true, CardRole::Pool},
                    {"BGS_034", 60558, 3, 2, 1, kTribeDragon, kKwDivineShield | kKwReborn, "TB_BaconUps_149", 63779, 4, 2, kKwDivineShield | kKwReborn, true, CardRole::Pool},
                    {"BG20_GEM", 70136, 0, 0, 0, 0, 0, "", 0, 0, 0, 0, true, CardRole::Spell},
            """;
        var rows = BgCards.ParseTable(inc);
        Assert.Equal(3, rows.Count);
        Assert.Equal(new BgTableRow("BGS_034", 2, 1, "TB_BaconUps_149", 4, 2, "Pool"), rows[1]);
        Assert.True(rows[2].IsSpell);
        Assert.Equal("", rows[2].GoldenId);
        Assert.Equal(["BGS_034", "TB_BaconUps_149"], BgCards.RenderCandidates("BGS_034"));
        Assert.Equal(["BG_EX1_534", "EX1_534"], BgCards.RenderCandidates("BG_EX1_534"));

        var donors = BgCards.StatDonors(rows, new HashSet<string>(), id => id != "BG19_010_G");
        Assert.Equal("BG19_010", donors["attack"][3]);
        Assert.Equal("BGS_034", donors["attack"][2]);
        Assert.False(donors["attack"].ContainsKey(6));   // the golden twin has no render
    }

    [Fact]
    public void Card_text_tokens_keep_bold_words()
    {
        var toks = CardImg.TokensFromMarkup("<b>Secret:</b> When your hero is <b>attacked</b>, gain $8 Armor.");
        Assert.Equal(
            [("Secret:", true), ("When", false), ("your", false), ("hero", false), ("is", false), ("attacked,", true),
             ("gain", false), ("8", false), ("Armor.", false)],
            toks);
    }

    [Fact]
    public void A_client_without_the_folder_is_not_installed()
    {
        using var root = new TempDir("hs");
        using var client = new TempDir("hs-client");
        var assets = new HearthstoneAssets(new LauncherPaths(root.Path));
        Assert.Equal(HearthstoneAssetState.NotInstalled, assets.StateFor(client.Path));
    }
}
