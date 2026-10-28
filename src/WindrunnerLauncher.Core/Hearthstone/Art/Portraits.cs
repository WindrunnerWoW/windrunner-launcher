using WindrunnerLauncher.Core.Hearthstone.Imaging;

namespace WindrunnerLauncher.Core.Hearthstone.Art;

/// <summary>
/// The cut-outs of the module's tools/art scripts that need no card data: oval minion portraits
/// (minion_portraits.py), round hero power portraits (power_portraits.py), padded card canvases
/// (convert_blp.py, hero_skins.py), the deck tile hero strips (slice_collection.py) and the Battlegrounds hero
/// shapes (bg_heroes.py).
/// </summary>
public static class Portraits
{
    public const int Size = 128;
    public const int TexW = 256;
    public const int TexH = 512;

    private const double CenterX = 128.5, TopY = 44.0, BottomY = 178.0;
    private const double Feather = 2.5;
    private const double Radius = Size / 2.0 - 1.0;

    private static readonly Lazy<PilImage> Oval = new(OvalMask);

    /// <summary>The power and quest portrait source circle on a 512 square: 41% of the width (power_portraits.API_RADIUS).</summary>
    public const double ApiRadius = 0.41 * 512;

    /// <summary>power_portraits.BOX: the art circle of a 256x388 card render.</summary>
    public static readonly (double, double, double, double) PowerRenderBox = (128.0 - 44.0, 112.0 - 44.0, 128.0 + 44.0, 112.0 + 44.0);

    /// <summary>L mask: 255 inside the inscribed circle, a linear ramp of 2.5 px at its edge (minion_portraits.oval_mask).</summary>
    public static PilImage OvalMask()
    {
        var m = new PilImage(PilMode.L, Size, Size);
        var c = (Size - 1) / 2.0;
        for (var y = 0; y < Size; y++)
        {
            for (var x = 0; x < Size; x++)
            {
                var r = Math.Sqrt(Math.Pow(x - c, 2) + Math.Pow(y - c, 2));
                var a = (Radius - r) / Feather + 0.5;
                m[x, y] = (byte)Py.Round(255 * Math.Max(0.0, Math.Min(1.0, a)));
            }
        }

        return m;
    }

    /// <summary>width / height of the minion ring's oval opening, from the board manifest (minion_portraits.target_aspect).</summary>
    public static double TargetAspect(double left, double top, double right, double bottom, int w, int h) =>
        (right - left) * w / ((bottom - top) * h);

    /// <summary>The art oval of a minion render cropped to the ring's aspect (minion_portraits.crop_box).</summary>
    public static (double, double, double, double) MinionCropBox(double aspect)
    {
        var h = BottomY - TopY;
        var w = h * aspect;
        return (CenterX - w / 2.0, TopY, CenterX + w / 2.0, BottomY);
    }

    /// <summary>The minion crop's width/height ratio on a spell frame's art window (bg_minions.spell_box).</summary>
    public static (double, double, double, double) SpellCropBox((double X0, double Y0, double X1, double Y1) box)
    {
        const double spellTop = 52.0, spellBottom = 178.0;
        var h = spellBottom - spellTop;
        var w = h * (box.X1 - box.X0) / (box.Y1 - box.Y0);
        return (128.0 - w / 2.0, spellTop, 128.0 + w / 2.0, spellBottom);
    }

    /// <summary>A 128x128 portrait of a box of the render with the round soft alpha (minion_portraits.portrait).</summary>
    public static PilImage Cut(PilImage render, (double, double, double, double) box)
    {
        var art = PilOps.Resize(render, Size, Size, Resampling.Lanczos, box);
        art.PutAlpha(Oval.Value);
        return art;
    }

    /// <summary>The round portrait from a 512x bare portrait (power_portraits.portrait, "api512").</summary>
    public static PilImage CutApi(PilImage art512)
    {
        var c = art512.Width / 2.0;
        var r = ApiRadius * art512.Width / 512.0;
        return Cut(art512, (c - r, c - r, c + r, c + r));
    }

    /// <summary>A render padded top-aligned onto a transparent 256x512 canvas (texcoord bottom = height / 512).</summary>
    public static PilImage CardCanvas(PilImage render)
    {
        if (render.Width != TexW || render.Height > TexH)
            throw new InvalidDataException($"render is {render.Width}x{render.Height}, expected {TexW} wide and at most {TexH} high");
        var canvas = new PilImage(PilMode.RGBA, TexW, TexH);
        canvas.Paste(render, 0, 0);
        return canvas;
    }

    /// <summary>
    /// The portrait strip of a hero card render for the deck tiles (slice_collection.hero_strip), with its
    /// transparent edge coloured (slice_board.bleed_color) and padded onto the texture (slice_board.compose_texture).
    /// </summary>
    public static PilImage HeroStrip(PilImage render, (double X, double Y) center, int w, int h, int cropW, int texW, int texH)
    {
        var ch = cropW * h / (double)w;
        var (cx, cy) = center;
        var x0 = Py.Round(cx - cropW / 2.0);
        var y0 = Py.Round(cy - ch / 2.0);
        var x1 = Py.Round(cx + cropW / 2.0);
        var y1 = Py.Round(cy + ch / 2.0);
        var strip = PilOps.Resize(render.Convert(PilMode.RGBa).Crop(x0, y0, x1, y1), w, h, Resampling.Lanczos).Convert(PilMode.RGBA);
        BleedColor(strip);
        var tex = new PilImage(PilMode.RGBA, texW, texH);
        tex.Paste(strip, 0, 0);
        return tex;
    }

    /// <summary>Gives fully transparent pixels the colour of their visible neighbours (slice_board.bleed_color).</summary>
    public static void BleedColor(PilImage im, int passes = 4)
    {
        int w = im.Width, h = im.Height;
        var known = new bool[w * h];
        for (var i = 0; i < w * h; i++)
            known[i] = im.Data[i * 4 + 3] > 0;
        (int, int)[] dirs = [(-1, 0), (1, 0), (0, -1), (0, 1), (-1, -1), (1, 1), (-1, 1), (1, -1)];
        for (var pass = 0; pass < passes; pass++)
        {
            var upd = new List<(int X, int Y, int R, int G, int B)>();
            for (var y = 0; y < h; y++)
            {
                for (var x = 0; x < w; x++)
                {
                    if (known[y * w + x])
                        continue;
                    int sr = 0, sg = 0, sb = 0, n = 0;
                    foreach (var (dx, dy) in dirs)
                    {
                        int xx = x + dx, yy = y + dy;
                        if (xx < 0 || xx >= w || yy < 0 || yy >= h || !known[yy * w + xx])
                            continue;
                        var (r, g, b, _) = im.GetRgba(xx, yy);
                        sr += r;
                        sg += g;
                        sb += b;
                        n++;
                    }

                    if (n > 0)
                        upd.Add((x, y, sr / n, sg / n, sb / n));
                }
            }

            foreach (var (x, y, r, g, b) in upd)
            {
                im.SetRgba(x, y, r, g, b, 0);
                known[y * w + x] = true;
            }
        }
    }

    // ------------------------------------------------------------------ Battlegrounds heroes (bg_heroes.py)

    private static double Smooth(double a, double b, double x)
    {
        var t = Math.Max(0.0, Math.Min(1.0, (x - a) / (b - a)));
        return t * t * (3.0 - 2.0 * t);
    }

    /// <summary>A rounded-square alpha with a soft one pixel edge and one transparent pixel of margin (bg_heroes.rounded).</summary>
    public static PilImage Rounded(PilImage rgb, int radius)
    {
        var im = rgb.Convert(PilMode.RGBA);
        int w = im.Width, h = im.Height;
        for (var y = 0; y < h; y++)
        {
            for (var x = 0; x < w; x++)
            {
                var dx = Math.Max(Math.Max(radius - 0.5 - x, x - (w - 1 - radius + 0.5)), 0.0);
                var dy = Math.Max(Math.Max(radius - 0.5 - y, y - (h - 1 - radius + 0.5)), 0.0);
                var corner = dx > 0 && dy > 0;
                var d = corner ? double.Hypot(dx, dy) : Math.Max(dx, dy);
                var edge = Math.Min(Math.Min(x, y), Math.Min(w - 1 - x, h - 1 - y)) + 0.5;
                var a = corner
                    ? (1.0 - Smooth(radius - 1.5, radius + 0.5, d)) * Smooth(0.5, 1.5, edge)
                    : Smooth(0.5, 1.5, edge);
                im.Data[(y * w + x) * 4 + 3] = (byte)Py.Round(255 * a);
            }
        }

        return im;
    }

    /// <summary>An arch alpha: a dome over the full width on top, straight sides below (bg_heroes.arch).</summary>
    public static PilImage Arch(PilImage rgb)
    {
        var im = rgb.Convert(PilMode.RGBA);
        int w = im.Width, h = im.Height;
        var rx = (w - 2) / 2.0;
        var ry = rx * 0.9;
        var cx = (w - 1) / 2.0;
        for (var y = 0; y < h; y++)
        {
            for (var x = 0; x < w; x++)
            {
                double inside;
                if (y < ry + 1)
                {
                    var d = double.Hypot((x - cx) / rx, (y - (ry + 1)) / ry);
                    inside = 1.0 - Smooth(1.0 - 1.5 / rx, 1.0 + 0.5 / rx, d);
                }
                else
                {
                    inside = Smooth(0.5, 1.5, Math.Min(x, w - 1 - x) + 0.5);
                }

                if (y > h - 2)
                    inside *= y > h - 1 ? 0.0 : 0.6;
                im.Data[(y * w + x) * 4 + 3] = (byte)Py.Round(255 * inside);
            }
        }

        return im;
    }

    private static PilImage CropResizeRgb(PilImage art, (int X0, int Y0, int X1, int Y1) box, int size) =>
        PilOps.Resize(art.Convert(PilMode.RGB).Crop(box.X0, box.Y0, box.X1, box.Y1), size, size, Resampling.Lanczos);

    /// <summary>Bob as an arch (bg_heroes.cut_bob).</summary>
    public static PilImage CutBob(PilImage art512) => Arch(CropResizeRgb(art512, (56, 8, 456, 488), 128));

    /// <summary>The face and shoulders as an opaque rectangle for the arena arch (bg_heroes.cut_arch).</summary>
    public static PilImage CutArch(PilImage art512) => CropResizeRgb(art512, (48, 8, 464, 488), 256).Convert(PilMode.RGBA);

    /// <summary>A 64x64 rounded-square hero portrait (bg_heroes.cut_hero).</summary>
    public static PilImage CutHero(PilImage art512) => Rounded(CropResizeRgb(art512, (64, 0, 448, 384), 64), 9);
}
