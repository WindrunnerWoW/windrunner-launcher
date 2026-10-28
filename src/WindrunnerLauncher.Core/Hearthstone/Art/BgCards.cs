using System.Text.RegularExpressions;
using WindrunnerLauncher.Core.Hearthstone.Imaging;

namespace WindrunnerLauncher.Core.Hearthstone.Art;

/// <summary>One row of the engine's Battlegrounds card table (src/battlegrounds/HsBgCardData.inc).</summary>
public sealed record BgTableRow(string Id, int Attack, int Health, string GoldenId, int GoldenAttack, int GoldenHealth, string Role)
{
    public bool IsSpell => Role == "Spell";
}

/// <summary>The full Battlegrounds cards of the module's tools/art/bg_minions.py and bg_quests.py that need repainting or baking.</summary>
public static class BgCards
{
    private static readonly Regex Row = new(
        "^\\s*\\{\"(?<id>[A-Za-z0-9_]+)\",\\s*\\d+,\\s*\\d+,\\s*(?<atk>\\d+),\\s*(?<hp>\\d+),[^\"]*\"(?<gid>[A-Za-z0-9_]*)\",\\s*\\d+,\\s*(?<gatk>\\d+),\\s*(?<ghp>\\d+),.*CardRole::(?<role>[A-Za-z]+)\\},\\s*$",
        RegexOptions.CultureInvariant);

    /// <summary>The rows of HsBgCardData.inc, in engine order.</summary>
    public static List<BgTableRow> ParseTable(string inc)
    {
        var rows = new List<BgTableRow>();
        foreach (var line in inc.Split('\n'))
        {
            var m = Row.Match(line.TrimEnd('\r'));
            if (!m.Success)
                continue;
            rows.Add(new BgTableRow(m.Groups["id"].Value, int.Parse(m.Groups["atk"].Value), int.Parse(m.Groups["hp"].Value),
                m.Groups["gid"].Value, int.Parse(m.Groups["gatk"].Value), int.Parse(m.Groups["ghp"].Value), m.Groups["role"].Value));
        }

        return rows;
    }

    /// <summary>
    /// The render ids to try for a table id, best first (bg_minions.render_candidates): its own, a golden or "up"
    /// render for the three minions without a normal one, the constructed twin of a BG_ variant.
    /// </summary>
    public static List<string> RenderCandidates(string cid)
    {
        var outList = new List<string> { cid };
        if (RenderFallback.TryGetValue(cid, out var fallback))
            outList.Add(fallback);
        if (cid.StartsWith("BG_", StringComparison.Ordinal) && cid.Length > 3)
            outList.Add(cid[3..]);
        return outList;
    }

    private static readonly Dictionary<string, string> RenderFallback = new()
    {
        ["BG23_357"] = "BG23_357_G",      // Mind Muck
        ["BG24_009"] = "BG24_009_G",      // Picky Eater
        ["BGS_034"] = "TB_BaconUps_149",  // Bronze Warden
    };

    // The whole stat gem with room for two digits (native 256x388).
    private static readonly Dictionary<string, (int, int, int, int)> BalanceGemBoxes = new()
    {
        ["attack"] = (18, 302, 73, 342),
        ["health"] = (186, 302, 244, 342),
    };

    private const int DigitShift = 26;
    private const int GemFrameX = 21;
    private const int OneLeftX = 23;

    /// <summary>Renders that show the golden card for the normal id: their gems and the word "double" are fixed.</summary>
    public static readonly IReadOnlyDictionary<string, (int Attack, int Health)> RenderFixes = new Dictionary<string, (int, int)>
    {
        ["BG24_009"] = (1, 1),
        ["BG23_357"] = (3, 2),
    };

    private static readonly Dictionary<string, ((int, int) Rows, (int, int) Line, (int, int) Cut, (int, int) Fill)> DoubleCut = new()
    {
        ["BG24_009"] = ((284, 299), (70, 183), (93, 137), (300, 310)),
        ["BG23_357"] = ((294, 308), (65, 197), (103, 149), (289, 294)),
    };

    /// <summary>
    /// {"attack"/"health": {value: render id}}: a cached render printing that value in that gem (bg_minions.stat_donors).
    /// The rebalanced and render-fixed cards are no donors.
    /// </summary>
    public static Dictionary<string, Dictionary<int, string>> StatDonors(IEnumerable<BgTableRow> table, ISet<string> skip, Func<string, bool> cached)
    {
        var donors = new Dictionary<string, Dictionary<int, string>> { ["attack"] = [], ["health"] = [] };
        foreach (var r in table.Where(r => !r.IsSpell).OrderBy(r => r.Id, StringComparer.Ordinal))
        {
            if (skip.Contains(r.Id))
                continue;
            foreach (var (id, atk, hp) in new[] { (r.Id, r.Attack, r.Health), (r.GoldenId, r.GoldenAttack, r.GoldenHealth) })
            {
                if (id.Length == 0 || !cached(id))
                    continue;
                donors["attack"].TryAdd(atk, id);
                donors["health"].TryAdd(hp, id);
            }
        }

        return donors;
    }

    /// <summary>
    /// Repaints the attack / health gems of a render (bg_minions.repaint_stats). A two-digit attack no render prints
    /// is built from the donor of "1&lt;units&gt;" with the tens digit taken from "1&lt;tens&gt;".
    /// </summary>
    public static void RepaintStats(PilImage im, IEnumerable<(string Gem, int Value)> want,
        Dictionary<string, Dictionary<int, string>> donors, Func<string, PilImage> loadLo)
    {
        foreach (var (gem, value) in want)
        {
            var box = BalanceGemBoxes[gem];
            if (donors[gem].TryGetValue(value, out var donor))
            {
                CardImg.GemSwap(im, loadLo(donor), box);
                continue;
            }

            var tens = value / 10;
            var units = value % 10;
            donors[gem].TryGetValue(10 + units, out var bse);
            donors[gem].TryGetValue(10 + tens, out var glyph);
            if (gem != "attack" || !(1 < tens && tens < 10) || bse is null || glyph is null)
                throw new InvalidDataException($"no donor for {gem} {value}");
            CardImg.GemSwap(im, loadLo(bse), box);
            var (x0, y0, x1, y1) = box;
            var mid = (x0 + x1) / 2;
            var cell = loadLo(glyph).Crop(mid, y0, x1, y1);
            var left = im.Crop(mid - DigitShift, y0, x1 - DigitShift, y1);
            var ox = mid - DigitShift;
            var glyphMask = PilOps.MaxFilter(DigitMask(cell), 3);
            var oneMask = PilOps.MaxFilter(DigitMask(left), 3);
            oneMask.PasteColor(0, (0, 0, OneLeftX - ox, oneMask.Height));
            var mask = new PilImage(PilMode.L, cell.Width, cell.Height);
            mask.PasteColor(255, null, glyphMask);
            mask.PasteColor(255, null, oneMask);
            mask = PilOps.GaussianBlur(mask, 0.6f);
            mask.PasteColor(0, (0, 0, GemFrameX - ox, mask.Height));
            im.Paste(cell, ox, y0, mask);
        }
    }

    /// <summary>255 on the pixels of a stat digit: near-white unsaturated fill or near-black outline (bg_minions.digit_mask).</summary>
    private static PilImage DigitMask(PilImage cell)
    {
        var o = new PilImage(PilMode.L, cell.Width, cell.Height);
        for (var y = 0; y < cell.Height; y++)
        {
            for (var x = 0; x < cell.Width; x++)
            {
                var (r, g, b, a) = cell.GetRgba(x, y);
                int hi = Math.Max(r, Math.Max(g, b)), lo = Math.Min(r, Math.Min(g, b));
                if (a > 128 && ((hi > 205 && hi - lo < 45) || hi < 70))
                    o[x, y] = 255;
            }
        }

        return o;
    }

    public static bool HasDoubleCut(string id) => DoubleCut.ContainsKey(id);

    /// <summary>Cuts "double " out of the last rules line, the words on either side moved together (bg_minions.cut_double).</summary>
    public static void CutDouble(PilImage im, string key, PilImage orig)
    {
        var ((y0, y1), (lx, rx), (c0, c1), (f0, f1)) = DoubleCut[key];
        const int gap = 3;
        var left = orig.Crop(lx, y0, c0, y1);
        var right = orig.Crop(c1 - 1, y0, rx, y1);
        var band = orig.Crop(lx - 3, f0, rx + 4, f1);
        var flipped = band.FlipTopBottom();
        for (var k = 0; k < y1 - y0; k += band.Height)
            im.Paste((k / band.Height) % 2 == 0 ? band : flipped, lx - 3, y0 + k);
        var total = left.Width + gap + right.Width;
        var start = Py.Round((lx + rx) / 2.0 - total / 2.0);
        im.Paste(left, start, y0, left);
        im.Paste(right, start + left.Width + gap, y0, right);
    }

    // ------------------------------------------------------------------ bg_quests.bake_card

    private const int FrameW = 256, FrameH = 388;
    private static readonly (int Left, int Top, int Right, int Bottom) CardBox = (16, 38, 229, 341);

    /// <summary>
    /// The owner's empty reward card scaled to 256x388 and the mask of its art window (bg_quests.build_frame);
    /// <paramref name="blank"/> is tools/art/assets/reward_frame_blank.png.
    /// </summary>
    public static (PilImage Frame, PilImage Mask) BuildRewardFrame(PilImage blank)
    {
        var bbox = blank.Band(3).GetBBox() ?? throw new InvalidDataException("the blank reward frame is empty");
        var src = blank.Crop(bbox.X0, bbox.Y0, bbox.X1, bbox.Y1);
        var (left, top, right, _) = CardBox;
        var k = (right - left) / (double)src.Width;
        var frame = new PilImage(PilMode.RGBA, FrameW, FrameH);
        frame.Paste(PilOps.Resize(src, right - left, Py.Round(src.Height * k), Resampling.Lanczos), left, top);
        var flat = PilImage.New(PilMode.RGBA, FrameW, FrameH, PilImage.Rgba(255, 255, 255));
        flat.AlphaComposite(frame);
        var dark = PilOps.MinFilter(flat.Convert(PilMode.L).Point(v => v < 75 ? 255 : 0), 3);
        dark.FloodFill(left + (int)(205 * k), top + (int)(140 * k), 128);
        var mask = PilOps.MinFilter(PilOps.MaxFilter(dark.Point(v => v == 128 ? 255 : 0), 5), 5);
        var outside = mask.Copy();
        outside.FloodFill(0, 0, 128);
        mask = outside.Point(v => v == 128 ? 0 : 255);
        mask.FillRectangle(0, 0, left + (int)(48 * k), FrameH, 0);
        mask.FillRectangle(left + (int)(304 * k), 0, FrameW, FrameH, 0);
        mask = PilOps.GaussianBlur(PilOps.MinFilter(mask, 3), 0.8f);
        return (frame, mask);
    }

    /// <summary>The 256x388 card of a reward with no render: the empty frame with its art in the window (bg_quests.bake_card).</summary>
    public static PilImage BakeRewardCard((PilImage Frame, PilImage Mask) frame, PilImage art512)
    {
        var box = frame.Mask.GetBBox() ?? throw new InvalidDataException("empty reward window mask");
        int bw = box.X1 - box.X0, bh = box.Y1 - box.Y0;
        var k = Math.Max(bw / (double)art512.Width, bh / (double)art512.Height);
        var art = PilOps.Resize(art512, (int)(art512.Width * k) + 1, (int)(art512.Height * k) + 1, Resampling.Lanczos);
        int ox = (art.Width - bw) / 2, oy = (art.Height - bh) / 2;
        var layer = new PilImage(PilMode.RGBA, FrameW, FrameH);
        layer.Paste(art.Crop(ox, oy, ox + bw, oy + bh), box.X0, box.Y0);
        var o = frame.Frame.Copy();
        o.Paste(layer, 0, 0, frame.Mask);
        return o;
    }
}
