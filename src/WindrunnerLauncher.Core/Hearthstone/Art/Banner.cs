using System.Text.RegularExpressions;
using WindrunnerLauncher.Core.Hearthstone.Imaging;

namespace WindrunnerLauncher.Core.Hearthstone.Art;

/// <summary>Baseline y(x) of a banner title (hi-res) for one card type: a polynomial in (x - 200) (banner.Curve).</summary>
public sealed class BannerCurve(IReadOnlyList<double> coefs, double centerX, double letterGap, double spaceGap)
{
    public IReadOnlyList<double> Coefs { get; } = coefs;
    public double CenterX { get; } = centerX;
    public double LetterGap { get; } = letterGap;
    public double SpaceGap { get; } = spaceGap;

    public double Y(double x)
    {
        var d = x - 200.0;
        double s = 0;
        for (var i = 0; i < Coefs.Count; i++)
            s += Coefs[i] * Math.Pow(d, i);
        return s;
    }

    public double Slope(double x)
    {
        var d = x - 200.0;
        double s = 0;
        for (var i = 1; i < Coefs.Count; i++)
            s += i * Coefs[i] * Math.Pow(d, i - 1);
        return s;
    }

    /// <summary>Counter-clockwise tilt in degrees (baseline rising to the right is positive).</summary>
    public double Ccw(double x) => Math.Atan(-Slope(x)) * (180.0 / Math.PI);
}

/// <summary>Banner title editing of the module's tools/art/banner.py: letters cut out of other titles and re-set along the banner curve.</summary>
internal static class Banner
{
    public static readonly (int X0, int Y0, int X1, int Y1) NameRoi = (90, 352, 432, 446);
    private const int NameOutline = 3;
    private static readonly (int R, int G, int B) OutlineRgb = (22, 18, 16);
    private static readonly Regex TitleChars = new("^[A-Za-z ]+$", RegexOptions.CultureInvariant);

    internal sealed class Cluster
    {
        public int[] BBox { get; init; } = new int[4];   // x0, y0, x1, y1 inclusive
        public HashSet<(int X, int Y)> Pix { get; init; } = [];
    }

    internal sealed class Glyph
    {
        public char Ch { get; init; }
        public string Src { get; init; } = "";
        public PilImage Img { get; init; } = null!;
        public (int X0, int Y0, int X1, int Y1) Fill { get; init; }
        public (double X, double Y) Pivot { get; init; }
        public int Area { get; init; }
    }

    /// <summary>White letter fills of the title, fragments of one letter merged by x proximity (banner.fill_clusters).</summary>
    public static List<Cluster> FillClusters(PilImage im, int thr = 232, int gap = 3, int minarea = 3)
    {
        var (x0, y0, x1, y1) = NameRoi;
        int w = x1 - x0, h = y1 - y0;
        var m = new bool[w * h];
        for (var j = 0; j < h; j++)
        {
            for (var i = 0; i < w; i++)
            {
                var (r, g, b, a) = im.GetRgba(x0 + i, y0 + j);
                if (a > 250 && r >= thr && g >= thr && b >= thr)
                    m[j * w + i] = true;
            }
        }

        var lab = new bool[w * h];
        var frags = new List<Cluster>();
        var stack = new Stack<int>();
        for (var s = 0; s < w * h; s++)
        {
            if (!m[s] || lab[s])
                continue;
            stack.Push(s);
            lab[s] = true;
            var pts = new List<int>();
            while (stack.Count > 0)
            {
                var q = stack.Pop();
                pts.Add(q);
                int qx = q % w, qy = q / w;
                for (var dx = -1; dx <= 1; dx++)
                {
                    for (var dy = -1; dy <= 1; dy++)
                    {
                        int xx = qx + dx, yy = qy + dy;
                        if (xx < 0 || xx >= w || yy < 0 || yy >= h)
                            continue;
                        var r = yy * w + xx;
                        if (m[r] && !lab[r])
                        {
                            lab[r] = true;
                            stack.Push(r);
                        }
                    }
                }
            }

            if (pts.Count < minarea)
                continue;
            var xs = pts.Select(p => p % w + x0).ToList();
            var ys = pts.Select(p => p / w + y0).ToList();
            frags.Add(new Cluster
            {
                BBox = [xs.Min(), ys.Min(), xs.Max(), ys.Max()],
                Pix = pts.Select(p => (p % w + x0, p / w + y0)).ToHashSet()
            });
        }

        var sorted = frags.OrderBy(f => f.BBox[0]).ToList();   // stable, like Python's sort
        var outList = new List<Cluster>();
        foreach (var f in sorted)
        {
            if (outList.Count > 0 && f.BBox[0] <= outList[^1].BBox[2] + gap)
            {
                var o = outList[^1];
                o.BBox[0] = Math.Min(o.BBox[0], f.BBox[0]);
                o.BBox[1] = Math.Min(o.BBox[1], f.BBox[1]);
                o.BBox[2] = Math.Max(o.BBox[2], f.BBox[2]);
                o.BBox[3] = Math.Max(o.BBox[3], f.BBox[3]);
                o.Pix.UnionWith(f.Pix);
            }
            else
            {
                outList.Add(f);
            }
        }

        return outList;
    }

    private static (List<Cluster> Clusters, List<char> Letters)? AlignedTitle(IRenderStore renders, string cid, string name)
    {
        if (!TitleChars.IsMatch(name))
            return null;
        var letters = name.Where(c => c != ' ').ToList();
        var cl = FillClusters(renders.LoadHi(cid));
        return cl.Count == letters.Count ? (cl, letters) : null;
    }

    private static PilImage Rotate(PilImage premultipliedRgba, double angle, (double, double) pivot, (double, double) translate) =>
        PilOps.Rotate(premultipliedRgba, angle, Resampling.Bicubic, pivot, translate);

    /// <summary>Upright glyphs (letter fill plus outline, RGBA) of a title, or none if it does not align (banner.harvest_glyphs).</summary>
    public static List<Glyph> HarvestGlyphs(IRenderStore renders, string cid, string name, BannerCurve curve)
    {
        var at = AlignedTitle(renders, cid, name);
        if (at is null)
            return [];
        var (clusters, letters) = at.Value;
        var im = renders.LoadHi(cid);
        var allFill = new HashSet<(int, int)>();
        foreach (var c in clusters)
            allFill.UnionWith(c.Pix);
        var result = new List<Glyph>();
        for (var ci = 0; ci < clusters.Count; ci++)
        {
            var c = clusters[ci];
            var ch = letters[ci];
            int x0 = c.BBox[0], y0 = c.BBox[1], x1 = c.BBox[2], y1 = c.BBox[3];
            const int pad = NameOutline + 9;
            int bx0 = x0 - pad, by0 = y0 - pad, bx1 = x1 + pad + 1, by1 = y1 + pad + 1;
            int w = bx1 - bx0, h = by1 - by0;
            var fm = new PilImage(PilMode.L, w, h);
            foreach (var (x, y) in c.Pix)
                fm[x - bx0, y - by0] = 255;
            var closed = PilOps.MinFilter(PilOps.MaxFilter(fm, 5), 5);
            var forb = new PilImage(PilMode.L, w, h);
            foreach (var (x, y) in allFill)
                if (!c.Pix.Contains((x, y)) && bx0 <= x && x < bx1 && by0 <= y && y < by1)
                    forb[x - bx0, y - by0] = 255;
            forb = PilOps.MaxFilter(forb, 5);
            var near = PilOps.MaxFilter(closed, 2 * (NameOutline + 2) + 1);
            var ring = new List<double>();
            for (var y = 0; y < h; y++)
            {
                for (var x = 0; x < w; x++)
                {
                    if (near[x, y] < 128 && closed[x, y] < 128 && forb[x, y] < 128)
                    {
                        var (r, g, b, _) = im.GetRgba(bx0 + x, by0 + y);
                        ring.Add(Py.Lum(r, g, b));
                    }
                }
            }

            var bgL = ring.Count > 0 ? Py.Median(ring) : 170.0;
            var patch = new PilImage(PilMode.RGBA, w, h);
            for (var y = 0; y < h; y++)
            {
                for (var x = 0; x < w; x++)
                {
                    if (forb[x, y] > 128 && closed[x, y] < 128)
                        continue;
                    var (r, g, b, _) = im.GetRgba(bx0 + x, by0 + y);
                    if (closed[x, y] >= 128)
                    {
                        patch.SetRgba(x, y, r, g, b, 255);
                    }
                    else if (near[x, y] >= 128)
                    {
                        var a = Py.Clamp01((bgL - Py.Lum(r, g, b)) / (bgL - 30.0));
                        if (a > 0.05)
                            patch.SetRgba(x, y, OutlineRgb.R, OutlineRgb.G, OutlineRgb.B, (int)(a * 255 + 0.5));
                    }
                }
            }

            var gx = (x0 + x1) / 2.0;
            var pivot = (gx - bx0 + 0.5, curve.Y(gx) - by0 + 0.5);
            var angle = -curve.Ccw(gx);
            var rot = BlpWriter.Unpremultiply(Rotate(patch.MultiplyByAlpha(), angle, pivot, (0.0, 0.0)));
            var fmRot = PilOps.Rotate(closed, angle, Resampling.Bilinear, pivot, (0.0, 0.0));
            var bb = fmRot.Point(v => v >= 128 ? 255 : 0).GetBBox() ?? (0, 0, 0, 0);
            result.Add(new Glyph
            {
                Ch = ch,
                Src = cid,
                Img = rot,
                Fill = bb,
                Pivot = pivot,
                Area = closed.Data.Count(v => v >= 128)
            });
        }

        return result;
    }

    /// <summary>Removes the old title: dilated fills and outline, the banner interpolated vertically (banner._erase_title).</summary>
    private static void EraseTitle(PilImage im, List<Cluster> clusters)
    {
        var (x0, y0, x1, y1) = NameRoi;
        int w = x1 - x0, h = y1 - y0;
        var fill = new PilImage(PilMode.L, w, h);
        foreach (var c in clusters)
            foreach (var (x, y) in c.Pix)
                fill[x - x0, y - y0] = 255;
        var near = PilOps.MaxFilter(fill, 2 * (NameOutline + 4) + 1);
        var ink = new PilImage(PilMode.L, w, h);
        for (var y = 0; y < h; y++)
        {
            for (var x = 0; x < w; x++)
            {
                if (near[x, y] <= 128)
                    continue;
                var (r, g, b, _) = im.GetRgba(x0 + x, y0 + y);
                if (Py.Lum(r, g, b) < 105 || Math.Min(r, Math.Min(g, b)) >= 205)
                    ink[x, y] = 255;
            }
        }

        var mask = PilOps.MaxFilter(ink, 5);
        for (var x = 0; x < w; x++)
        {
            var y = 0;
            while (y < h)
            {
                if (mask[x, y] > 128)
                {
                    var ys = y;
                    while (y < h && mask[x, y] > 128)
                        y++;
                    var ye = y;
                    var a = ys - 1;
                    var b = ye;
                    if (a < 0 || b >= h)
                        continue;
                    var ca = new List<(int R, int G, int B, int A)>();
                    for (var k = 0; k < 2; k++)
                        if (a - k >= 0)
                        {
                            var p = im.GetRgba(x0 + x, y0 + a - k);
                            ca.Add((p.R, p.G, p.B, p.A));
                        }

                    var cb = new List<(int R, int G, int B, int A)>();
                    for (var k = 0; k < 2; k++)
                        if (b + k < h)
                        {
                            var p = im.GetRgba(x0 + x, y0 + b + k);
                            cb.Add((p.R, p.G, p.B, p.A));
                        }

                    double ar = (double)ca.Sum(c => c.R) / ca.Count, ag = (double)ca.Sum(c => c.G) / ca.Count, ab = (double)ca.Sum(c => c.B) / ca.Count;
                    double br = (double)cb.Sum(c => c.R) / cb.Count, bg = (double)cb.Sum(c => c.G) / cb.Count, bb = (double)cb.Sum(c => c.B) / cb.Count;
                    for (var yy = ys; yy < ye; yy++)
                    {
                        var t = (yy - a) / (double)(b - a);
                        im.SetRgba(x0 + x, y0 + yy, (int)(ar * (1 - t) + br * t + 0.5), (int)(ag * (1 - t) + bg * t + 0.5),
                            (int)(ab * (1 - t) + bb * t + 0.5), 255);
                    }
                }
                else
                {
                    y++;
                }
            }
        }
    }

    /// <summary>
    /// The hi-res render of <paramref name="cid"/> with its banner title replaced by <paramref name="newName"/>
    /// (banner.render_title). <paramref name="cards"/> gives (name, type) per card id.
    /// </summary>
    public static (PilImage Image, (int, int, int, int) Box) RenderTitle(IRenderStore renders, string cid, string newName,
        IReadOnlyDictionary<string, BannerCurve> curves, IReadOnlyList<string> donors,
        IReadOnlyDictionary<string, (string Name, string Type)> cards, IReadOnlyDictionary<char, string>? pick)
    {
        var im = renders.LoadHi(cid);
        var curve = curves[cards[cid].Type];
        var bank = new Dictionary<char, List<Glyph>>();
        foreach (var d in donors)
        {
            var dc = cards[d];
            foreach (var g in HarvestGlyphs(renders, d, dc.Name, curves[dc.Type]))
            {
                if (!bank.TryGetValue(g.Ch, out var list))
                    bank[g.Ch] = list = [];
                list.Add(g);
            }
        }

        var old = FillClusters(im).Where(c => Math.Abs(c.BBox[3] - curve.Y((c.BBox[0] + c.BBox[2]) / 2.0)) < 40).ToList();
        EraseTitle(im, old);
        var seq = new List<Glyph?>();
        foreach (var ch in newName)
        {
            if (ch == ' ')
            {
                seq.Add(null);
                continue;
            }

            if (!bank.TryGetValue(ch, out var cand) || cand.Count == 0)
                throw new InvalidDataException($"no glyph for '{ch}'; add a donor title containing it");
            var hs = cand.OrderBy(g => g.Area).ThenBy(g => g.Src, StringComparer.Ordinal).ToList();
            var chosen = hs[hs.Count / 2];
            if (pick is not null && pick.TryGetValue(ch, out var src))
                chosen = hs.First(g => g.Src == src);
            seq.Add(chosen);
        }

        var xs = new List<(Glyph G, double A)>();
        var x = 0.0;
        foreach (var g in seq)
        {
            if (g is null)
            {
                x += curve.SpaceGap - curve.LetterGap;
                continue;
            }

            var fw = g.Fill.X1 - g.Fill.X0;
            xs.Add((g, x));
            x += fw + curve.LetterGap;
        }

        var total = x - curve.LetterGap;
        var xStart = curve.CenterX - total / 2.0;
        foreach (var (g, a) in xs)
        {
            var pxLeft = xStart + a;
            var gx = pxLeft + (g.Pivot.X - g.Fill.X0);
            var gy = curve.Y(gx);
            var ang = curve.Ccw(gx);
            var big = new PilImage(PilMode.RGBA, g.Img.Width + 40, g.Img.Height + 40);
            big.Paste(g.Img, 20, 20);
            var pv = (X: g.Pivot.X + 20, Y: g.Pivot.Y + 20);
            int ix = (int)Math.Floor(gx), iy = (int)Math.Floor(gy);
            double fx = gx - ix, fy = gy - iy;
            var rot = BlpWriter.Unpremultiply(Rotate(big.MultiplyByAlpha(), ang, pv, (fx, fy)));
            im.AlphaComposite(rot, ix - (int)pv.X, iy - (int)pv.Y);
        }

        return (im, NameRoi);
    }
}
