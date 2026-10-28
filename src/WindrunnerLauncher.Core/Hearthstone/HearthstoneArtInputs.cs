using System.Text.Json.Nodes;
using WindrunnerLauncher.Core.Hearthstone.Art;

namespace WindrunnerLauncher.Core.Hearthstone;

/// <summary>
/// The files of the Hearthstone module repository the art build reads: the committed manifests that list every
/// texture and its source, the fixes, the Battlegrounds engine table and the owner's blank reward frame.
/// Paths are relative to the repository root.
/// </summary>
public sealed class HearthstoneArtInputs
{
    public const string RendersManifest = "data/art/renders.manifest.json";
    public const string Fixes = "data/art/fixes.json";
    public const string BannerCurves = "data/art/banner_curves.json";
    public const string MinionsManifest = "data/art/minions.manifest.json";
    public const string PowersManifest = "data/art/powers.manifest.json";
    public const string HeroSkinsManifest = "data/art/hero_skins.manifest.json";
    public const string BgMinionsManifest = "data/art/bg_minions.manifest.json";
    public const string BgHeroesManifest = "data/art/bg_heroes.manifest.json";
    public const string BgQuestsManifest = "data/art/bg_quests.manifest.json";
    public const string BoardManifest = "data/art/board_textures.manifest.json";
    public const string CollectionPieces = "data/art/collection_pieces.json";
    public const string BalanceOverrides = "data/battlegrounds/balance_overrides.json";
    public const string BgTable = "src/battlegrounds/HsBgCardData.inc";
    public const string RewardFrameBlank = "tools/art/assets/reward_frame_blank.png";
    public const string TexturesManifest = "data/art/textures.manifest.json";
    public const string CollectionManifest = "data/art/collection_textures.manifest.json";

    public static readonly IReadOnlyList<string> Files =
    [
        RendersManifest, Fixes, BannerCurves, MinionsManifest, PowersManifest, HeroSkinsManifest, BgMinionsManifest,
        BgHeroesManifest, BgQuestsManifest, BoardManifest, CollectionPieces, BalanceOverrides, BgTable, RewardFrameBlank,
        TexturesManifest, CollectionManifest
    ];

    /// <summary>
    /// The textures the module commits (the owner's own art, no Blizzard pixels): repository folder -> folder under
    /// Interface\Hearthstone. The Collection folder also receives the generated hero strips.
    /// </summary>
    public static readonly IReadOnlyList<(string RepoFolder, string Target)> CommittedTextureFolders =
    [
        ("art/out/Board/", "Board"),
        ("art/out/UI/", "UI"),
        ("art/out/Collection/", "Collection"),
        ("art/out/Effects/", "Effects"),
        ("art/out/Bg/", "Bg"),
        // where module revisions before the Interface\Hearthstone move kept the effect and Battlegrounds textures
        ("addon/Hearthstone/Textures/Effects/", "Effects"),
        ("addon/Hearthstone/Textures/Bg/", "Bg"),
    ];

    private readonly string _root;

    public HearthstoneArtInputs(string repositoryRoot)
    {
        _root = repositoryRoot;
        foreach (var f in Files)
            if (!File.Exists(PathOf(f)))
                throw new FileNotFoundException($"Hearthstone module file missing: {f}", PathOf(f));
        Renders = Json(RendersManifest)["entries"]!.AsObject();
        FixesDoc = Json(Fixes);
        Curves = RenderPatcher.ParseCurves(Json(BannerCurves));
        Minions = Json(MinionsManifest)["entries"]!.AsObject();
        Powers = Json(PowersManifest)["entries"]!.AsObject();
        HeroSkins = Json(HeroSkinsManifest)["entries"]!.AsObject();
        BgMinions = Json(BgMinionsManifest);
        BgHeroes = Json(BgHeroesManifest);
        BgQuests = Json(BgQuestsManifest);
        Board = Json(BoardManifest);
        Collection = Json(CollectionPieces);
        Balance = Json(BalanceOverrides)["cards"]!.AsObject();
        Table = BgCards.ParseTable(File.ReadAllText(PathOf(BgTable)));
    }

    public string PathOf(string relative) => Path.Combine(_root, relative.Replace('/', Path.DirectorySeparatorChar));

    private JsonNode Json(string relative) =>
        JsonNode.Parse(File.ReadAllText(PathOf(relative))) ?? throw new InvalidDataException($"{relative} is empty");

    public JsonObject Renders { get; }
    public JsonNode FixesDoc { get; }
    public IReadOnlyDictionary<string, BannerCurve> Curves { get; }
    public JsonObject Minions { get; }
    public JsonObject Powers { get; }
    public JsonObject HeroSkins { get; }
    public JsonNode BgMinions { get; }
    public JsonNode BgHeroes { get; }
    public JsonNode BgQuests { get; }
    public JsonNode Board { get; }
    public JsonNode Collection { get; }
    public JsonObject Balance { get; }
    public IReadOnlyList<BgTableRow> Table { get; }

    public IEnumerable<JsonNode> FixList => FixesDoc["fixes"]!.AsArray().Select(f => f!);

    public IEnumerable<string> HiresSources => Strings(FixesDoc["hires_sources"]);
    public IEnumerable<string> ArtSources => Strings(FixesDoc["art_sources"]);

    /// <summary>The constructed art set: renders.manifest.json entries that have a render.</summary>
    public IEnumerable<string> CardIds =>
        Renders.Where(kv => kv.Value!["status"]!.GetValue<string>() != "missing").Select(kv => kv.Key).Order(StringComparer.Ordinal);

    public string? SourceUrl(string id) => Renders[id]?["source_url"]?.GetValue<string>();
    public string? SourceId(string id) => Renders[id]?["source_id"]?.GetValue<string>();

    public (double, double, double, double) MinionBox()
    {
        var p = Board["pieces"]!["minion_oval"]!;
        var o = p["anchors"]!["oval"]!;
        var aspect = Portraits.TargetAspect(o["left"]!.GetValue<double>(), o["top"]!.GetValue<double>(),
            o["right"]!.GetValue<double>(), o["bottom"]!.GetValue<double>(), p["w"]!.GetValue<int>(), p["h"]!.GetValue<int>());
        return Portraits.MinionCropBox(aspect);
    }

    /// <summary>Blizzard-derived BLPs the module's manifests expect, relative to Interface\Hearthstone, with their reference SHA-256.</summary>
    public Dictionary<string, string?> ExpectedHashes()
    {
        var expected = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        if (Json(TexturesManifest)["entries"] is JsonObject cards)
            foreach (var (id, e) in cards)
                expected[$"Cards/{id}.blp"] = e?["blp_sha256"]?.GetValue<string>();
        foreach (var (id, e) in Minions)
            expected[$"Minions/{id}.blp"] = e?["blp_sha256"]?.GetValue<string>();
        foreach (var (id, e) in Powers)
            expected[$"Powers/{id}.blp"] = e?["blp_sha256"]?.GetValue<string>();
        foreach (var (id, e) in HeroSkins)
            expected[$"Heroes/{id}.blp"] = e?["blp_sha256"]?.GetValue<string>();
        foreach (var (id, e) in BgMinions["entries"]!.AsObject())
            expected[$"BgMinions/{id}.blp"] = e?["blp_sha256"]?.GetValue<string>();
        foreach (var (id, e) in BgMinions["cards"]!.AsObject())
            expected[$"BgMinions/Cards/{id}.blp"] = e?["sha256"]?.GetValue<string>();
        foreach (var (key, e) in BgHeroes["entries"]!.AsObject())
            expected[$"BgHeroes/{key}.blp"] = e?["sha256"]?.GetValue<string>();
        foreach (var (key, e) in BgHeroes["powers"]!.AsObject())
            expected[$"BgHeroes/Powers/{key}.blp"] = e?["sha256"]?.GetValue<string>();
        foreach (var (key, e) in BgHeroes["powerCards"]?.AsObject() ?? [])
            expected[$"BgHeroes/PowerCards/{key}.blp"] = e?["sha256"]?.GetValue<string>();
        foreach (var (key, e) in BgQuests["entries"]!.AsObject())
            expected[$"BgQuests/{key}.blp"] = e?["sha256"]?.GetValue<string>();
        foreach (var (key, e) in BgQuests["cards"]?.AsObject() ?? [])
            expected[$"BgQuests/Cards/{key}.blp"] = e?["sha256"]?.GetValue<string>();
        if (Json(CollectionManifest)["pieces"] is JsonObject pieces)
            foreach (var (name, e) in pieces)
                if (name.StartsWith("hero_", StringComparison.Ordinal))
                    expected[$"Collection/{name}.blp"] = e?["blp_sha256"]?.GetValue<string>();
        return expected;
    }

    private static IEnumerable<string> Strings(JsonNode? node) =>
        node is JsonArray a ? a.Select(x => x!.GetValue<string>()) : [];
}
