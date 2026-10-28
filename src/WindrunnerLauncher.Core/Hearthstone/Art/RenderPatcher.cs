using System.Text.Json.Nodes;
using WindrunnerLauncher.Core.Hearthstone.Imaging;

namespace WindrunnerLauncher.Core.Hearthstone.Art;

/// <summary>What the fixes need to know about a card of the art set.</summary>
public sealed record ArtCard(string Id, string Name, string Type, string? Acquisition, string? CardClass, string? Rarity);

/// <summary>
/// Applies data/art/fixes.json to the downloaded renders (the module's tools/art/patch_renders.py): stat gems
/// copied from donors, rules text and banner titles rebuilt from other renders, art windows filled.
/// </summary>
public sealed class RenderPatcher
{
    private static readonly Dictionary<string, (int, int, int, int)> GemBoxes = new()
    {
        ["cost"] = (26, 47, 56, 87),
        ["attack"] = (30, 299, 59, 339),
        ["health"] = (198, 302, 227, 339),
    };

    private static readonly Dictionary<string, (int, int, int, int)> TextRoi = new()
    {
        ["SPELL"] = (102, 492, 410, 626),
        ["MINION"] = (100, 478, 412, 632),
        ["WEAPON"] = (100, 478, 412, 632),
    };

    private static readonly Dictionary<string, (int, int, int, int)[]> TextExclude = new()
    {
        ["SPELL"] = [],
        ["MINION"] = [(100, 574, 150, 632), (368, 566, 412, 632)],
        ["WEAPON"] = [(100, 574, 150, 632), (368, 566, 412, 632)],
    };

    private readonly IRenderStore _renders;
    private readonly IReadOnlyDictionary<string, ArtCard> _cards;
    private readonly IReadOnlyDictionary<string, string> _texts;
    private readonly IReadOnlyDictionary<string, BannerCurve> _curves;
    private readonly Func<string, PilImage?> _artOnly;

    /// <param name="cards">The cards of renders.manifest.json (name, type, acquisition) with class and rarity.</param>
    /// <param name="texts">Card id -> rules text of the render's source card in the current HearthstoneJSON data.</param>
    /// <param name="curves">data/art/banner_curves.json.</param>
    /// <param name="artOnly">The 512x portrait art of a card (art.hearthstonejson.com/v1/512x), or null.</param>
    public RenderPatcher(IRenderStore renders, IReadOnlyDictionary<string, ArtCard> cards,
        IReadOnlyDictionary<string, string> texts, IReadOnlyDictionary<string, BannerCurve> curves,
        Func<string, PilImage?> artOnly)
    {
        _renders = renders;
        _cards = cards;
        _texts = texts;
        _curves = curves;
        _artOnly = artOnly;
    }

    public static Dictionary<string, BannerCurve> ParseCurves(JsonNode curvesJson)
    {
        var curves = new Dictionary<string, BannerCurve>();
        foreach (var (type, node) in curvesJson.AsObject())
        {
            var coefs = node!["coefs"]!.AsArray().Select(c => c!.GetValue<double>()).ToList();
            curves[type] = new BannerCurve(coefs, node["center_x"]!.GetValue<double>(),
                node["letter_gap"]!.GetValue<double>(), node["space_gap"]!.GetValue<double>());
        }

        return curves;
    }

    /// <summary>The patched 256x388 render of one fixes.json entry (patch_renders.run_fix).</summary>
    public PilImage Apply(JsonNode fix)
    {
        var cid = fix["id"]!.GetValue<string>();
        var bse = fix["template"]?.GetValue<string>() ?? cid;
        var typ = Card(bse).Type;
        var lo = _renders.LoadLo(bse);
        var ops = fix["ops"]!.AsArray();
        var hiRegions = new List<(PilImage Im, (int, int, int, int) Box)>();
        foreach (var op in ops)
        {
            switch (op!["op"]!.GetValue<string>())
            {
                case "text":
                {
                    var roi = op["roi"] is JsonArray r ? Box(r) : TextRoi[typ];
                    var lines = op["lines"] is JsonArray l
                        ? l.Select(x => x!.GetValue<string>()).ToList()
                        : throw new NotSupportedException($"{cid}: a text op without explicit lines is not supported");
                    var donors = Strings(op["donors"]);
                    var texts = new Dictionary<string, string>(_texts);
                    if (op["text_overrides"] is JsonObject overrides)
                        foreach (var (k, v) in overrides)
                            texts[k] = v!.GetValue<string>();
                    var compose = op["compose"] is JsonObject c ? ParseCompose(c) : null;
                    hiRegions.Add(CardImg.RenderText(_renders, bse, lines, donors, roi, TextExclude[typ], texts, compose));
                    break;
                }
                case "title":
                {
                    var donors = Strings(op["donors"]);
                    var pick = op["glyphs"] is JsonObject g
                        ? g.ToDictionary(kv => kv.Key[0], kv => kv.Value!.GetValue<string>())
                        : null;
                    var names = donors.Append(bse).Distinct().ToDictionary(id => id, id => (Card(id).Name, Card(id).Type));
                    hiRegions.Add(Banner.RenderTitle(_renders, bse, op["name"]!.GetValue<string>(), _curves, donors, names, pick));
                    break;
                }
            }
        }

        foreach (var (im, box) in hiRegions)
            CardImg.PasteRegionLo(lo, im, box);

        foreach (var op in ops)
        {
            if (op!["op"]!.GetValue<string>() != "art")
                continue;
            var c = Card(bse);
            var frame = _cards.Values
                .Where(x => x.CardClass == c.CardClass && x.Type == c.Type && x.Acquisition == "standard"
                            && x.Rarity != "LEGENDARY" && _renders.HasLo(x.Id))
                .Select(x => x.Id)
                .OrderBy(id => id, StringComparer.Ordinal)
                .ToList();
            var mask = ArtWindow.WindowMask(_renders, frame);
            var image = op["image"]?.GetValue<string>();
            if (image is not null)
            {
                var art = _artOnly(image) ?? throw new InvalidDataException($"{cid}: no portrait art {image}.jpg");
                var offset = op["offset"]!.AsArray();
                ArtWindow.PasteArt(lo, mask, art, op["size"]!.GetValue<int>(),
                    (offset[0]!.GetValue<int>(), offset[1]!.GetValue<int>()));
            }
            else
            {
                ArtWindow.Placeholder(lo, mask);
            }
        }

        foreach (var op in ops)
        {
            if (op!["op"]!.GetValue<string>() != "gem")
                continue;
            CardImg.GemSwap(lo, _renders.LoadLo(op["donor"]!.GetValue<string>()), GemBoxes[op["gem"]!.GetValue<string>()]);
        }

        return lo;
    }

    private ArtCard Card(string id) =>
        _cards.TryGetValue(id, out var c) ? c : throw new InvalidDataException($"card {id} is not in the art set");

    private static List<string> Strings(JsonNode? node) =>
        node is JsonArray a ? a.Select(x => x!.GetValue<string>()).ToList() : [];

    private static (int, int, int, int) Box(JsonArray a) =>
        (a[0]!.GetValue<int>(), a[1]!.GetValue<int>(), a[2]!.GetValue<int>(), a[3]!.GetValue<int>());

    private static Dictionary<string, IReadOnlyList<CardImg.ComposePart>> ParseCompose(JsonObject compose)
    {
        var result = new Dictionary<string, IReadOnlyList<CardImg.ComposePart>>();
        foreach (var (token, parts) in compose)
        {
            result[token] = parts!.AsArray().Select(p => new CardImg.ComposePart(
                p!["token"]!.GetValue<string>(),
                p["bold"]?.GetValue<bool>() ?? false,
                p["first"]?.GetValue<int>(),
                p["last"]?.GetValue<int>(),
                p["gap"]?.GetValue<int>())).ToList();
        }

        return result;
    }
}
