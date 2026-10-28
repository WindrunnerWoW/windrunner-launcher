using WindrunnerLauncher.Core.Hearthstone.Imaging;

namespace WindrunnerLauncher.Core.Hearthstone.Art;

/// <summary>The art window of a card frame, and filling it (the module's tools/art/artwin.py).</summary>
internal static class ArtWindow
{
    /// <summary>
    /// Mask (L, 255 inside) of the art window of a frame: the pixels that vary between renders sharing the
    /// frame (artwin.window_mask). <paramref name="frameIds"/> are those renders, sorted by id.
    /// </summary>
    public static PilImage WindowMask(IRenderStore renders, IReadOnlyList<string> frameIds, int limit = 40)
    {
        var ims = frameIds.Take(limit).Select(id => renders.LoadLo(id).Convert(PilMode.L)).ToList();
        if (ims.Count == 0)
            throw new InvalidDataException("no renders to find the art window with");
        int w = ims[0].Width, h = ims[0].Height, n = ims.Count;
        var m = new PilImage(PilMode.L, w, h);
        for (var y = 0; y < h; y++)
        {
            for (var x = 0; x < w; x++)
            {
                double sum = 0;
                foreach (var im in ims)
                    sum += im[x, y];
                var mean = sum / n;
                double var = 0;
                foreach (var im in ims)
                    var += Math.Pow(im[x, y] - mean, 2);
                var /= n;
                if (var > 15 * 15)
                    m[x, y] = 255;
            }
        }

        // keep the biggest blob, close small holes
        m = PilOps.MinFilter(PilOps.MaxFilter(m, 5), 5);
        var seen = new bool[w * h];
        var best = new List<(int X, int Y)>();
        var stack = new Stack<(int X, int Y)>();
        for (var y = 0; y < h; y++)
        {
            for (var x = 0; x < w; x++)
            {
                if (m[x, y] == 0 || seen[y * w + x])
                    continue;
                stack.Push((x, y));
                seen[y * w + x] = true;
                var comp = new List<(int X, int Y)>();
                while (stack.Count > 0)
                {
                    var (a, b) = stack.Pop();
                    comp.Add((a, b));
                    foreach (var (dx, dy) in new[] { (1, 0), (-1, 0), (0, 1), (0, -1) })
                    {
                        int qx = a + dx, qy = b + dy;
                        if (qx >= 0 && qx < w && qy >= 0 && qy < h && m[qx, qy] != 0 && !seen[qy * w + qx])
                        {
                            seen[qy * w + qx] = true;
                            stack.Push((qx, qy));
                        }
                    }
                }

                if (comp.Count > best.Count)
                    best = comp;
            }
        }

        var outIm = new PilImage(PilMode.L, w, h);
        foreach (var (x, y) in best)
            outIm[x, y] = 255;
        // fill holes: flood the outside, invert
        var inv = PilImage.New(PilMode.L, w + 2, h + 2, 255);
        foreach (var (a, b) in best)
            inv[a + 1, b + 1] = 0;
        var outside = new bool[(w + 2) * (h + 2)];
        outside[0] = true;
        stack.Push((0, 0));
        while (stack.Count > 0)
        {
            var (a, b) = stack.Pop();
            foreach (var (dx, dy) in new[] { (1, 0), (-1, 0), (0, 1), (0, -1) })
            {
                int qx = a + dx, qy = b + dy;
                if (qx >= 0 && qx < w + 2 && qy >= 0 && qy < h + 2 && inv[qx, qy] != 0 && !outside[qy * (w + 2) + qx])
                {
                    outside[qy * (w + 2) + qx] = true;
                    stack.Push((qx, qy));
                }
            }
        }

        for (var y = 0; y < h; y++)
            for (var x = 0; x < w; x++)
                if (!outside[(y + 1) * (w + 2) + x + 1])
                    outIm[x, y] = 255;
        return PilOps.MaxFilter(outIm, 3);   // cover the last art pixels under the frame rim
    }

    /// <summary>Fills the window with a soft grey vignette, the pipeline's "art missing" look (artwin.placeholder).</summary>
    public static void Placeholder(PilImage lo, PilImage mask)
    {
        (int R, int G, int B) bse = (158, 158, 164);
        var bb = mask.GetBBox() ?? throw new InvalidDataException("empty art window mask");
        double cx = (bb.X0 + bb.X1) / 2.0, cy = (bb.Y0 + bb.Y1) / 2.0;
        double rx = (bb.X1 - bb.X0) / 2.0, ry = (bb.Y1 - bb.Y0) / 2.0;
        var fill = new PilImage(PilMode.RGBA, lo.Width, lo.Height);
        for (var y = bb.Y0; y < bb.Y1; y++)
        {
            for (var x = bb.X0; x < bb.X1; x++)
            {
                var d = Math.Pow(Math.Pow((x - cx) / rx, 2) + Math.Pow((y - cy) / ry, 2), 0.5);
                var k = 1.0 - 0.28 * Math.Pow(Math.Min(1.0, d), 2);
                fill.SetRgba(x, y, (int)(bse.R * k), (int)(bse.G * k), (int)(bse.B * k), 255);
            }
        }

        lo.Paste(fill, 0, 0, PilOps.GaussianBlur(mask, 0.7f));
    }

    /// <summary>Pastes real portrait art scaled to size x size at offset, clipped to the window (artwin.paste_art).</summary>
    public static void PasteArt(PilImage lo, PilImage mask, PilImage art, int size, (int X, int Y) offset)
    {
        var a = PilOps.Resize(art, size, size, Resampling.Lanczos);
        var layer = new PilImage(PilMode.RGBA, lo.Width, lo.Height);
        layer.Paste(a, offset.X, offset.Y);
        lo.Paste(layer, 0, 0, PilOps.GaussianBlur(mask, 0.7f));
    }
}
