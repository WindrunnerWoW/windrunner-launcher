using System.Text.RegularExpressions;
using WindrunnerLauncher.Core.Hearthstone.Imaging;

namespace WindrunnerLauncher.Core.Hearthstone.Art;

/// <summary>The downloaded card renders: 256x388 ("lo") and 512x776 ("hi") PNGs of HearthstoneJSON.</summary>
public interface IRenderStore
{
    /// <summary>The unpatched 256x render of a card (art/cache/&lt;id&gt;.png), a fresh copy.</summary>
    PilImage LoadLo(string id);

    /// <summary>The 512x render of a card (art/cache/512/&lt;id&gt;.png), a fresh copy.</summary>
    PilImage LoadHi(string id);

    bool HasLo(string id);
}

/// <summary>A word image cut out of a render: coverage (L) cropped to the word, and its baseline row.</summary>
internal sealed class Word(string token, bool bold, PilImage alpha, int baseline, string src)
{
    public string Token { get; } = token;
    public bool Bold { get; } = bold;
    public PilImage Alpha { get; } = alpha;
    public int Baseline { get; } = baseline;
    public string Src { get; } = src;
}

internal sealed class TextLine
{
    public int Top { get; init; }
    public int Bottom { get; init; }
    public int Baseline { get; init; }
    public List<(int A, int B)> Words { get; init; } = [];
}

/// <summary>Text analysis of one render: smooth background, ink colour and per-pixel coverage (cardimg.TextBox).</summary>
internal sealed class TextBox
{
    public (int X0, int Y0, int X1, int Y1) Roi { get; }
    public int W { get; }
    public int H { get; }
    public (double R, double G, double B) T { get; }
    public PilImage Bg { get; }
    public double[,] Alpha { get; }
    private readonly bool _darkText;

    public TextBox(PilImage im, (int X0, int Y0, int X1, int Y1) roi, IReadOnlyList<(int A, int B, int C, int D)> exclude)
    {
        Roi = roi;
        var (x0, y0, x1, y1) = roi;
        W = x1 - x0;
        H = y1 - y0;
        var crop = im.Crop(x0, y0, x1, y1).Convert(PilMode.RGB);
        var excl = new bool[W, H];
        foreach (var (a, b, c, d) in exclude)
            for (var yy = Math.Max(b, y0) - y0; yy < Math.Min(d, y1) - y0; yy++)
                for (var xx = Math.Max(a, x0) - x0; xx < Math.Min(c, x1) - x0; xx++)
                    excl[xx, yy] = true;

        double LumAt(int x, int y)
        {
            var o = (y * W + x) * 4;
            return Py.Lum(crop.Data[o], crop.Data[o + 1], crop.Data[o + 2]);
        }

        var lums = new List<double>();
        for (var y = 0; y < H; y++)
            for (var x = 0; x < W; x++)
                if (!excl[x, y])
                    lums.Add(LumAt(x, y));
        lums.Sort();
        var n = lums.Count;
        var med = lums[n / 2];
        var loP = lums[(int)(n * 0.012)];
        var hiP = lums[Math.Min(n - 1, (int)(n * 0.988))];
        _darkText = med - loP >= hiP - med;
        var cut = _darkText ? loP : hiP;
        long sr = 0, sg = 0, sb = 0;
        var count = 0;
        for (var y = 0; y < H; y++)
        {
            for (var x = 0; x < W; x++)
            {
                if (excl[x, y])
                    continue;
                var v = LumAt(x, y);
                if (_darkText ? v <= cut : v >= cut)
                {
                    var o = (y * W + x) * 4;
                    sr += crop.Data[o];
                    sg += crop.Data[o + 1];
                    sb += crop.Data[o + 2];
                    count++;
                }
            }
        }

        T = ((double)sr / count, (double)sg / count, (double)sb / count);
        var ranked = _darkText ? PilOps.MaxFilter(crop, 11) : PilOps.MinFilter(crop, 11);
        Bg = PilOps.GaussianBlur(ranked, 4);
        Alpha = new double[H, W];
        for (var y = 0; y < H; y++)
        {
            for (var x = 0; x < W; x++)
            {
                if (excl[x, y])
                    continue;
                var o = (y * W + x) * 4;
                int b0 = Bg.Data[o], b1 = Bg.Data[o + 1], b2 = Bg.Data[o + 2];
                int p0 = crop.Data[o], p1 = crop.Data[o + 1], p2 = crop.Data[o + 2];
                double d0 = T.R - b0, d1 = T.G - b1, d2 = T.B - b2;
                var den = d0 * d0 + d1 * d1 + d2 * d2;
                if (den < 900)
                    continue;
                var t = ((p0 - b0) * d0 + (p1 - b1) * d1 + (p2 - b2) * d2) / den;
                t = Py.Clamp01(t);
                Alpha[y, x] = t > 0.08 ? t : 0.0;
            }
        }

        DropFrame();
    }

    /// <summary>Keeps only text-like connected areas of the coverage map (cardimg.TextBox._drop_frame).</summary>
    private void DropFrame()
    {
        var seen = new bool[W * H];
        var keep = new PilImage(PilMode.L, W, H);
        var stack = new Stack<(int X, int Y)>();
        for (var sy = 0; sy < H; sy++)
        {
            for (var sx = 0; sx < W; sx++)
            {
                if (seen[sy * W + sx] || Alpha[sy, sx] <= 0.3)
                    continue;
                stack.Push((sx, sy));
                seen[sy * W + sx] = true;
                var pts = new List<(int X, int Y)>();
                while (stack.Count > 0)
                {
                    var (x, y) = stack.Pop();
                    pts.Add((x, y));
                    for (var dx = -1; dx <= 1; dx++)
                    {
                        for (var dy = -1; dy <= 1; dy++)
                        {
                            int xx = x + dx, yy = y + dy;
                            if (xx >= 0 && xx < W && yy >= 0 && yy < H && !seen[yy * W + xx] && Alpha[yy, xx] > 0.3)
                            {
                                seen[yy * W + xx] = true;
                                stack.Push((xx, yy));
                            }
                        }
                    }
                }

                int minX = pts.Min(p => p.X), maxX = pts.Max(p => p.X), minY = pts.Min(p => p.Y), maxY = pts.Max(p => p.Y);
                int bw = maxX - minX + 1, bh = maxY - minY + 1;
                var ratio = pts.Count / (double)(bw * bh);
                var junk = minX < 8 || minY < 4 || maxX > W - 9 || maxY > H - 5 || pts.Count < 6
                           || (ratio < 0.18 && (bw > 40 || bh > 40)) || bh > 60;
                if (!junk)
                    foreach (var (x, y) in pts)
                        keep[x, y] = 255;
            }
        }

        var near = PilOps.MaxFilter(keep, 7);
        for (var y = 0; y < H; y++)
            for (var x = 0; x < W; x++)
                if (near[x, y] == 0)
                    Alpha[y, x] = 0.0;
    }

    public bool[,] Mask(double thr = 0.45)
    {
        var m = new bool[H, W];
        for (var y = 0; y < H; y++)
            for (var x = 0; x < W; x++)
                m[y, x] = Alpha[y, x] > thr;
        // frame edges inside the search box show up as long straight lines: drop them
        for (var x = 0; x < W; x++)
        {
            var sum = 0;
            for (var y = 0; y < H; y++)
                if (m[y, x])
                    sum++;
            if (sum > 0.55 * H)
                for (var y = 0; y < H; y++)
                    m[y, x] = false;
        }

        for (var y = 0; y < H; y++)
        {
            var sum = 0;
            for (var x = 0; x < W; x++)
                if (m[y, x])
                    sum++;
            if (sum > 0.8 * W)
                for (var x = 0; x < W; x++)
                    m[y, x] = false;
        }

        return m;
    }
}

/// <summary>Image operations of the module's tools/art/cardimg.py (gem swap and rules text), on 512x renders.</summary>
internal static class CardImg
{
    public const int Hi = 2;

    private static readonly int[] GapOptions = [6, 5, 7, 4, 8];

    /// <summary>cardimg.box_down: 2x2 box average, premultiplied so transparent pixels do not bleed.</summary>
    public static PilImage BoxDown(PilImage im) =>
        BlpWriter.Unpremultiply(PilOps.Resize(im.MultiplyByAlpha(), im.Width / Hi, im.Height / Hi, Resampling.Box));

    /// <summary>cardimg.paste_region_lo: the box-downscaled hi-res region pasted into the native render.</summary>
    public static void PasteRegionLo(PilImage lo, PilImage hiEdited, (int X0, int Y0, int X1, int Y1) boxHi)
    {
        var (x0, y0, x1, y1) = boxHi;
        if (x0 % Hi != 0 || y0 % Hi != 0 || x1 % Hi != 0 || y1 % Hi != 0)
            throw new ArgumentException("hi-res boxes must use even coordinates");
        var part = BoxDown(hiEdited.Crop(x0, y0, x1, y1));
        lo.Paste(part, x0 / Hi, y0 / Hi);
    }

    /// <summary>cardimg.gem_swap: copy a rectangle from the donor with a one pixel soft edge.</summary>
    public static void GemSwap(PilImage lo, PilImage donorLo, (int X0, int Y0, int X1, int Y1) box, int feather = 1)
    {
        var patch = donorLo.Crop(box.X0, box.Y0, box.X1, box.Y1);
        var mask = PilImage.New(PilMode.L, patch.Width, patch.Height, 255);
        if (feather > 0)
        {
            for (var y = 0; y < patch.Height; y++)
            {
                for (var x = 0; x < patch.Width; x++)
                {
                    var d = Math.Min(Math.Min(x, y), Math.Min(patch.Width - 1 - x, patch.Height - 1 - y));
                    if (d < feather + 1)
                        mask[x, y] = (byte)(int)(255 * (d + 0.5) / (feather + 1));
                }
            }
        }

        lo.Paste(patch, box.X0, box.Y0, mask);
    }

    /// <summary>Lines and words of a text box for every word-gap option (cardimg.segment).</summary>
    public static List<(int Gap, List<TextLine> Lines)> Segment(TextBox tb, double thr)
    {
        var m = tb.Mask(thr);
        var rowcnt = new int[tb.H];
        for (var y = 0; y < tb.H; y++)
            for (var x = 0; x < tb.W; x++)
                if (m[y, x])
                    rowcnt[y]++;
        var lineRuns = Py.Runs(rowcnt.Select(c => c >= 2).ToList(), minGap: 1);
        var lines = new List<(int T, int B, bool[] Cols)>();
        foreach (var (t, b) in lineRuns)
        {
            if (b - t < 8 || rowcnt[t..b].Max() < 12)
                continue;
            var cols = new bool[tb.W];
            for (var x = 0; x < tb.W; x++)
                for (var y = t; y < b && !cols[x]; y++)
                    cols[x] = m[y, x];
            lines.Add((t, b, cols));
        }

        var outList = new List<(int, List<TextLine>)>();
        foreach (var g in GapOptions)
        {
            var res = new List<TextLine>();
            foreach (var (t, b, cols) in lines)
            {
                var ws = Py.Runs(cols, minGap: g - 1)
                    .Where(r => !((r.A < 22 || r.B > tb.W - 22) && r.B - r.A < 24))
                    .ToList();
                if (ws.Count == 0)
                    continue;
                var mx = rowcnt[t..b].Max();
                var bse = t;
                for (var i = 0; i < b - t; i++)
                    if (rowcnt[t + i] >= 0.3 * mx)
                        bse = t + i;
                res.Add(new TextLine { Top = t, Bottom = b, Baseline = bse + 1, Words = ws });
            }

            outList.Add((g, res));
        }

        return outList;
    }

    private static readonly Regex TagSplit = new("(</?b>|</?i>)", RegexOptions.CultureInvariant);

    /// <summary>[(token, bold)] from card text with &lt;b&gt;/&lt;i&gt; markup (cardimg.tokens_from_markup).</summary>
    public static List<(string Token, bool Bold)> TokensFromMarkup(string? text)
    {
        text = (text ?? "").Replace("[x]", "").Replace("$", "").Replace("#", "").Replace(' ', ' ').Replace('\n', ' ');
        var bold = false;
        var chars = new List<(char C, bool Bold)>();
        foreach (var part in TagSplit.Split(text))
        {
            switch (part)
            {
                case "<b>": bold = true; break;
                case "</b>": bold = false; break;
                case "<i>" or "</i>": break;
                default:
                    foreach (var ch in part)
                        chars.Add((ch, bold));
                    break;
            }
        }

        var toks = new List<List<(char C, bool Bold)>>();
        var cur = new List<(char, bool)>();
        foreach (var (ch, bd) in chars)
        {
            if (char.IsWhiteSpace(ch))
            {
                if (cur.Count > 0)
                {
                    toks.Add(cur);
                    cur = [];
                }
            }
            else
            {
                cur.Add((ch, bd));
            }
        }

        if (cur.Count > 0)
            toks.Add(cur);
        var result = new List<(string, bool)>();
        foreach (var t in toks)
        {
            var s = new string(t.Select(c => c.C).ToArray());
            var core = t.Where(c => char.IsLetterOrDigit(c.C)).Select(c => c.Bold).ToList();
            if (core.Count == 0)
                core = t.Select(c => c.Bold).ToList();
            result.Add((s, core.All(b => b)));
        }

        return result;
    }

    /// <summary>
    /// Cuts the words out of a hi-res render (cardimg.harvest_words): words aligned with the tokens of the
    /// text, or null when the segmentation does not give the expected word count.
    /// </summary>
    public static (List<Word>? Words, TextBox Tb, List<TextLine>? Lines) HarvestWords(IRenderStore renders, string cid,
        string textMarkup, (int, int, int, int) roi, IReadOnlyList<(int, int, int, int)> exclude)
    {
        var im = renders.LoadHi(cid);
        var tb = new TextBox(im, roi, exclude);
        var toks = TokensFromMarkup(textMarkup);
        foreach (var thr in new[] { 0.35, 0.45, 0.55, 0.28 })
        {
            foreach (var (_, lines) in Segment(tb, thr))
            {
                var nwords = lines.Sum(l => l.Words.Count);
                if (nwords != toks.Count)
                    continue;
                var flat = lines.SelectMany(l => l.Words).ToList();
                var implausible = false;
                for (var i = 0; i < flat.Count; i++)
                {
                    var per = (flat[i].B - flat[i].A - 4) / (double)toks[i].Token.Length;
                    if (!(4.0 <= per && per <= 18.5))
                    {
                        implausible = true;
                        break;
                    }
                }

                if (implausible)
                    continue;
                var words = new List<Word>();
                var k = 0;
                foreach (var l in lines)
                {
                    foreach (var (a, b) in l.Words)
                    {
                        int a2 = Math.Max(0, a - 2), b2 = Math.Min(tb.W, b + 2);
                        int t = l.Top - 1, bt = l.Bottom + 1;
                        var crop = new PilImage(PilMode.L, b2 - a2, bt - t);
                        for (var y = t; y < bt; y++)
                        {
                            if (y < 0 || y >= tb.H)
                                continue;
                            for (var x = a2; x < b2; x++)
                            {
                                var v = tb.Alpha[y, x];
                                if (v > 0)
                                    crop[x - a2, y - t] = (byte)(int)(v * 255 + 0.5);
                            }
                        }

                        var (tok, bold) = toks[k];
                        words.Add(new Word(tok, bold, crop, l.Baseline - t, cid));
                        k++;
                    }
                }

                return (words, tb, lines);
            }
        }

        return (null, tb, null);
    }

    /// <summary>Paints the estimated background over the text of the box (cardimg.erase_text).</summary>
    public static void EraseText(PilImage im, TextBox tb, int pad = 3)
    {
        var (x0, y0, x1, y1) = tb.Roi;
        var m = new PilImage(PilMode.L, tb.W, tb.H);
        for (var y = 0; y < tb.H; y++)
            for (var x = 0; x < tb.W; x++)
                if (tb.Alpha[y, x] > 0.04)
                    m[x, y] = 255;
        m = PilOps.GaussianBlur(PilOps.MaxFilter(m, 2 * pad + 1), 1.2f);
        var crop = im.Crop(x0, y0, x1, y1).Convert(PilMode.RGB);
        var outIm = PilImage.Composite(tb.Bg, crop, m);
        im.Paste(outIm.Convert(PilMode.RGBA), x0, y0);
    }

    /// <summary>Composites a word image in the box's ink colour (cardimg.draw_word).</summary>
    public static void DrawWord(PilImage im, TextBox tb, Word word, int x, int yBase)
    {
        var top = yBase - word.Baseline;
        var t = tb.T;
        for (var yy = 0; yy < word.Alpha.Height; yy++)
        {
            var y = top + yy;
            if (y < 0 || y >= im.Height)
                continue;
            for (var xx = 0; xx < word.Alpha.Width; xx++)
            {
                int a = word.Alpha[xx, yy];
                if (a < 6)
                    continue;
                var px = x + xx;
                if (px < 0 || px >= im.Width)
                    continue;
                var k = a / 255.0;
                var (r, g, b, al) = im.GetRgba(px, y);
                im.SetRgba(px, y, (int)(r * (1 - k) + t.R * k + 0.5), (int)(g * (1 - k) + t.G * k + 0.5),
                    (int)(b * (1 - k) + t.B * k + 0.5), al);
            }
        }
    }

    private static (List<(int A, int B)> Runs, double[] Sums) ValleyRuns(PilImage alpha, double thr = 3)
    {
        var sums = new double[alpha.Width];
        for (var x = 0; x < alpha.Width; x++)
        {
            var s = 0;
            for (var y = 0; y < alpha.Height; y++)
                s += alpha[x, y];
            sums[x] = s / 255.0;
        }

        var ink = Enumerable.Range(0, alpha.Width).Where(i => sums[i] > thr).ToList();
        if (ink.Count == 0)
            return ([], sums);
        var flags = Enumerable.Range(ink[0], ink[^1] - ink[0] + 1).Select(i => sums[i] <= thr).ToList();
        var runs = Py.Runs(flags, minGap: 0).Select(r => (r.A + ink[0], r.B + ink[0])).ToList();
        return (runs, sums);
    }

    /// <summary>A part of a composed word (fixes.json "compose").</summary>
    public sealed record ComposePart(string Token, bool Bold, int? First, int? Last, int? Gap);

    /// <summary>Builds a word that exists on no render from pieces of other words (cardimg.compose_word).</summary>
    public static Word ComposeWord(string tok, bool bold, IReadOnlyList<ComposePart> parts, Func<string, bool, Word> lookup, int gap = 2)
    {
        var pieces = new List<(PilImage Im, int Baseline, int Gap)>();
        foreach (var p in parts)
        {
            var w = lookup(p.Token, p.Bold);
            var a = w.Alpha;
            int lo = 0, hi = a.Width;
            var (runs, sums) = ValleyRuns(a);
            if (p.First is { } first)
            {
                var r = runs[first - 1];
                hi = FloorDiv(r.A + r.B, 2);
            }
            else if (p.Last is { } last)
            {
                var r = runs[runs.Count - last];
                lo = FloorDiv(r.A + r.B, 2);
            }

            var inked = Enumerable.Range(lo, Math.Max(0, hi - lo)).Where(i => sums[i] > 0).ToList();
            lo = inked[0];
            hi = inked[^1] + 1;
            pieces.Add((a.Crop(lo, 0, hi, a.Height), w.Baseline, p.Gap ?? gap));
        }

        var above = pieces.Max(p => p.Baseline);
        var below = pieces.Max(p => p.Im.Height - p.Baseline);
        var width = 4 + pieces.Sum(p => p.Im.Width) + pieces.Skip(1).Sum(p => p.Gap);
        var outIm = new PilImage(PilMode.L, width, above + below);
        var x = 2;
        for (var i = 0; i < pieces.Count; i++)
        {
            if (i > 0)
                x += pieces[i].Gap;
            outIm.Paste(pieces[i].Im, x, above - pieces[i].Baseline);
            x += pieces[i].Im.Width;
        }

        return new Word(tok, bold, outIm, above, string.Join("+", parts.Select(p => p.Token)));
    }

    private static int FloorDiv(int a, int b) => (int)Math.Floor(a / (double)b);

    /// <summary>
    /// The hi-res render of <paramref name="cid"/> with its rules text replaced (cardimg.render_text), and the
    /// region (even hi-res coordinates) to paste back into the native render.
    /// </summary>
    public static (PilImage Image, (int, int, int, int) Box) RenderText(IRenderStore renders, string cid,
        IReadOnlyList<string> newLines, IReadOnlyList<string> sources, (int X0, int Y0, int X1, int Y1) roi,
        IReadOnlyList<(int, int, int, int)> exclude, IReadOnlyDictionary<string, string> cardsText,
        IReadOnlyDictionary<string, IReadOnlyList<ComposePart>>? compose)
    {
        var im = renders.LoadHi(cid);
        var (wordsSelf, tb, linesSelf) = HarvestWords(renders, cid, TextOf(cardsText, cid), roi, exclude);
        if (wordsSelf is null || linesSelf is null)
            throw new InvalidDataException("cannot segment the text of " + cid);
        var bank = new Dictionary<(string, bool), List<Word>>();

        void Add(IEnumerable<Word> words)
        {
            foreach (var w in words)
            {
                if (!bank.TryGetValue((w.Token, w.Bold), out var list))
                    bank[(w.Token, w.Bold)] = list = [];
                list.Add(w);
            }
        }

        Add(wordsSelf);
        foreach (var d in sources)
        {
            var (ws, _, _) = HarvestWords(renders, d, TextOf(cardsText, d), roi, exclude);
            if (ws is not null)
                Add(ws);
        }

        var baseOld = linesSelf.Select(l => l.Baseline).ToList();
        var gaps = new List<double>();
        foreach (var l in linesSelf)
            for (var i = 0; i + 1 < l.Words.Count; i++)
                gaps.Add(l.Words[i + 1].A - l.Words[i].B);
        var space = gaps.Count > 0 ? Py.Round(Py.Median(gaps)) : 8;
        var centers = linesSelf.Select(l => (l.Words[0].A + l.Words[^1].B) / 2.0);
        var cx = Py.Median(centers) + tb.Roi.X0;
        var pitch = baseOld.Count > 1
            ? Py.Median(baseOld.Zip(baseOld.Skip(1), (a, b) => (double)(b - a)))
            : 32;
        var blkCenter = (baseOld[0] + baseOld[^1]) / 2.0;

        Word Lookup(string tok, bool bold)
        {
            bank.TryGetValue((tok, bold), out var cand);
            if ((cand is null || cand.Count == 0) && compose is not null && compose.TryGetValue(tok, out var parts))
            {
                var w = ComposeWord(tok, bold, parts, Lookup);
                bank[(tok, bold)] = [w];
                return w;
            }

            if (cand is null || cand.Count == 0)
                throw new InvalidDataException($"no word image for '{tok}' (bold={bold}) in {cid}; add a donor that contains it");
            return cand[0];
        }

        var lineTokens = newLines.Select(TokensFromMarkup).ToList();
        var n = lineTokens.Count;
        var bases = n == baseOld.Count
            ? baseOld.Select(b => (double)b).ToList()
            : Enumerable.Range(0, n).Select(i => blkCenter + (i - (n - 1) / 2.0) * pitch).ToList();
        var yBases = bases.Select(b => Py.Round(b) + tb.Roi.Y0).ToList();

        EraseText(im, tb);
        for (var li = 0; li < n; li++)
        {
            var ws = lineTokens[li].Select(t => Lookup(t.Token, t.Bold)).ToList();
            var total = ws.Sum(w => w.Alpha.Width - 4) + space * (ws.Count - 1);
            var x = Py.Round(cx - total / 2.0);
            foreach (var w in ws)
            {
                DrawWord(im, tb, w, x - 2, yBases[li]);
                x += w.Alpha.Width - 4 + space;
            }
        }

        var box = (roi.X0 - roi.X0 % Hi, roi.Y0 - roi.Y0 % Hi, roi.X1 + roi.X1 % Hi, roi.Y1 + roi.Y1 % Hi);
        return (im, box);
    }

    private static string TextOf(IReadOnlyDictionary<string, string> cardsText, string id) =>
        cardsText.TryGetValue(id, out var t)
            ? t
            : throw new InvalidDataException($"no card text for {id} (HearthstoneJSON cards.json)");
}
