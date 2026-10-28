namespace WindrunnerLauncher.Core.Hearthstone.Imaging;

/// <summary>Resampling, filters and affine transforms ported from Pillow 12 (Resample.c, BoxBlur.c, RankFilter.c, Geometry.c).</summary>
public static class PilOps
{
    // ------------------------------------------------------------------ Resample.c

    private const int PrecisionBits = 32 - 8 - 2;

    private static double BoxFilter(double x) => x > -0.5 && x <= 0.5 ? 1.0 : 0.0;

    private static double BilinearFilter(double x)
    {
        if (x < 0.0)
            x = -x;
        return x < 1.0 ? 1.0 - x : 0.0;
    }

    private static double BicubicFilter(double x)
    {
        const double a = -0.5;
        if (x < 0.0)
            x = -x;
        if (x < 1.0)
            return ((a + 2.0) * x - (a + 3.0)) * x * x + 1;
        if (x < 2.0)
            return (((x - 5) * x + 8) * x - 4) * a;
        return 0.0;
    }

    private static double Sinc(double x)
    {
        if (x == 0.0)
            return 1.0;
        x *= Math.PI;
        return Math.Sin(x) / x;
    }

    private static double LanczosFilter(double x) => -3.0 <= x && x < 3.0 ? Sinc(x) * Sinc(x / 3) : 0.0;

    private static (Func<double, double> Filter, double Support) FilterOf(Resampling r) => r switch
    {
        Resampling.Box => (BoxFilter, 0.5),
        Resampling.Bilinear => (BilinearFilter, 1.0),
        Resampling.Bicubic => (BicubicFilter, 2.0),
        Resampling.Lanczos => (LanczosFilter, 3.0),
        _ => throw new NotSupportedException($"Resampling {r} is not ported.")
    };

    private static int PrecomputeCoeffs(int inSize, float in0, float in1, int outSize, Resampling resampling,
        out int[] bounds, out int[] kk)
    {
        var (filter, filterSupport) = FilterOf(resampling);
        double scale = (double)(in1 - in0) / outSize;
        var filterscale = scale;
        if (filterscale < 1.0)
            filterscale = 1.0;
        var support = filterSupport * filterscale;
        var ksize = (int)Math.Ceiling(support) * 2 + 1;
        var pre = new double[outSize * ksize];
        bounds = new int[outSize * 2];
        var invFilterscale = 1.0 / filterscale;
        for (var xx = 0; xx < outSize; xx++)
        {
            var center = in0 + (xx + 0.5) * scale;
            var ww = 0.0;
            var xmin = (int)(center - support + 0.5);
            if (xmin < 0)
                xmin = 0;
            var xmax = (int)(center + support + 0.5);
            if (xmax > inSize)
                xmax = inSize;
            xmax -= xmin;
            var k = xx * ksize;
            int x;
            for (x = 0; x < xmax; x++)
            {
                var w = filter((x + xmin - center + 0.5) * invFilterscale);
                pre[k + x] = w;
                ww += w;
            }

            if (ww != 0.0)
                for (x = 0; x < xmax; x++)
                    pre[k + x] /= ww;
            bounds[xx * 2] = xmin;
            bounds[xx * 2 + 1] = xmax;
        }

        kk = new int[pre.Length];
        for (var i = 0; i < pre.Length; i++)
            kk[i] = pre[i] < 0
                ? (int)(-0.5 + pre[i] * (1 << PrecisionBits))
                : (int)(0.5 + pre[i] * (1 << PrecisionBits));
        return ksize;
    }

    private static byte Clip8Fixed(int v)
    {
        var s = v >> PrecisionBits;
        return s < 0 ? (byte)0 : s > 255 ? (byte)255 : (byte)s;
    }

    /// <summary>
    /// <c>Image.resize(size, resample, box)</c>. RGBA is resampled premultiplied (converted to RGBa and back),
    /// as Pillow does for every filter but nearest.
    /// </summary>
    public static PilImage Resize(PilImage im, int width, int height, Resampling resampling,
        (double X0, double Y0, double X1, double Y1)? box = null)
    {
        var b = box ?? (0, 0, im.Width, im.Height);
        if (width == im.Width && height == im.Height && b == (0, 0, im.Width, im.Height))
            return im.Copy();
        if (resampling == Resampling.Nearest)
            throw new NotSupportedException("Nearest resize is not ported.");
        if (im.Mode == PilMode.RGBA)
            return Resize(im.Convert(PilMode.RGBa), width, height, resampling, box).Convert(PilMode.RGBA);
        return ResampleInner(im, width, height, resampling, ((float)b.X0, (float)b.Y0, (float)b.X1, (float)b.Y1));
    }

    private static PilImage ResampleInner(PilImage imIn, int xsize, int ysize, Resampling resampling,
        (float X0, float Y0, float X1, float Y1) box)
    {
        var needHorizontal = xsize != imIn.Width || box.X0 != 0 || box.X1 != xsize;
        var needVertical = ysize != imIn.Height || box.Y0 != 0 || box.Y1 != ysize;
        var ksizeVert = PrecomputeCoeffs(imIn.Height, box.Y0, box.Y1, ysize,
            resampling, out var boundsVert, out var kkVert);
        var yboxFirst = boundsVert[0];
        var yboxLast = boundsVert[ysize * 2 - 2] + boundsVert[ysize * 2 - 1];
        PilImage? imOut = null;
        if (needHorizontal)
        {
            var ksizeHoriz = PrecomputeCoeffs(imIn.Width, box.X0, box.X1, xsize, resampling, out var boundsHoriz, out var kkHoriz);
            for (var i = 0; i < ysize; i++)
                boundsVert[i * 2] -= yboxFirst;
            var temp = new PilImage(imIn.Mode, xsize, yboxLast - yboxFirst);
            Horizontal(temp, imIn, yboxFirst, ksizeHoriz, boundsHoriz, kkHoriz);
            imOut = imIn = temp;
        }

        if (needVertical)
        {
            imOut = new PilImage(imIn.Mode, imIn.Width, ysize);
            Vertical(imOut, imIn, ksizeVert, boundsVert, kkVert);
        }

        return imOut ?? imIn.Copy();
    }

    private static int BandsOf(PilMode mode) => mode switch
    {
        PilMode.L => 1,
        PilMode.RGB => 3,
        _ => 4
    };

    private static void Horizontal(PilImage imOut, PilImage imIn, int offset, int ksize, int[] bounds, int[] kk)
    {
        var ps = imIn.PixelSize;
        var bands = BandsOf(imIn.Mode);
        Span<int> ss = stackalloc int[4];
        for (var yy = 0; yy < imOut.Height; yy++)
        {
            var lineIn = (yy + offset) * imIn.Width * ps;
            var lineOut = yy * imOut.Width * ps;
            for (var xx = 0; xx < imOut.Width; xx++)
            {
                var xmin = bounds[xx * 2];
                var xmax = bounds[xx * 2 + 1];
                var k = xx * ksize;
                ss.Fill(1 << (PrecisionBits - 1));
                for (var x = 0; x < xmax; x++)
                {
                    var p = lineIn + (x + xmin) * ps;
                    for (var c = 0; c < bands; c++)
                        ss[c] += imIn.Data[p + c] * kk[k + x];
                }

                for (var c = 0; c < ps; c++)
                    imOut.Data[lineOut + xx * ps + c] = c < bands ? Clip8Fixed(ss[c]) : (byte)0;
            }
        }
    }

    private static void Vertical(PilImage imOut, PilImage imIn, int ksize, int[] bounds, int[] kk)
    {
        var ps = imIn.PixelSize;
        var bands = BandsOf(imIn.Mode);
        Span<int> ss = stackalloc int[4];
        for (var yy = 0; yy < imOut.Height; yy++)
        {
            var k = yy * ksize;
            var ymin = bounds[yy * 2];
            var ymax = bounds[yy * 2 + 1];
            var lineOut = yy * imOut.Width * ps;
            for (var xx = 0; xx < imOut.Width; xx++)
            {
                ss.Fill(1 << (PrecisionBits - 1));
                for (var y = 0; y < ymax; y++)
                {
                    var p = ((y + ymin) * imIn.Width + xx) * ps;
                    for (var c = 0; c < bands; c++)
                        ss[c] += imIn.Data[p + c] * kk[k + y];
                }

                for (var c = 0; c < ps; c++)
                    imOut.Data[lineOut + xx * ps + c] = c < bands ? Clip8Fixed(ss[c]) : (byte)0;
            }
        }
    }

    // ------------------------------------------------------------------ RankFilter.c + Filter.c expand

    /// <summary><c>ImageFilter.MaxFilter(size)</c>, per band.</summary>
    public static PilImage MaxFilter(PilImage im, int size) => RankFilter(im, size, max: true);

    /// <summary><c>ImageFilter.MinFilter(size)</c>, per band.</summary>
    public static PilImage MinFilter(PilImage im, int size) => RankFilter(im, size, max: false);

    private static PilImage RankFilter(PilImage im, int size, bool max)
    {
        if (size % 2 == 0)
            throw new ArgumentException("bad filter size");
        var o = new PilImage(im.Mode, im.Width, im.Height);
        var ps = im.PixelSize;
        var bands = ps == 1 ? 1 : im.Mode == PilMode.RGB ? 3 : 4;
        var margin = size / 2;
        var w = im.Width;
        var h = im.Height;
        // Separable: the extreme of a square window is the extreme of the column extremes. The border is the
        // edge pixel repeated (ImagingExpand).
        var tmp = new byte[w * h];
        for (var c = 0; c < bands; c++)
        {
            for (var y = 0; y < h; y++)
            {
                for (var x = 0; x < w; x++)
                {
                    int best = max ? 0 : 255;
                    for (var d = -margin; d <= margin; d++)
                    {
                        var yy = Math.Clamp(y + d, 0, h - 1);
                        int v = im.Data[(yy * w + x) * ps + c];
                        best = max ? Math.Max(best, v) : Math.Min(best, v);
                    }

                    tmp[y * w + x] = (byte)best;
                }
            }

            for (var y = 0; y < h; y++)
            {
                for (var x = 0; x < w; x++)
                {
                    int best = max ? 0 : 255;
                    for (var d = -margin; d <= margin; d++)
                    {
                        int v = tmp[y * w + Math.Clamp(x + d, 0, w - 1)];
                        best = max ? Math.Max(best, v) : Math.Min(best, v);
                    }

                    o.Data[(y * w + x) * ps + c] = (byte)best;
                }
            }
        }

        if (im.Mode == PilMode.RGB)
            for (var i = 3; i < o.Data.Length; i += 4)
                o.Data[i] = 255;
        return o;
    }

    // ------------------------------------------------------------------ BoxBlur.c

    /// <summary><c>ImageFilter.GaussianBlur(radius)</c>: three extended box blur passes per direction.</summary>
    public static PilImage GaussianBlur(PilImage im, float radius)
    {
        if (radius == 0)
            return im.Copy();
        var r = GaussianBlurRadius(radius, 3);
        return BoxBlur(im, r, r, 3);
    }

    private static float GaussianBlurRadius(float radius, int passes)
    {
        float sigma2 = radius * radius / passes;
        var bigL = (float)Math.Sqrt(12.0 * sigma2 + 1.0);
        var l = (float)Math.Floor((bigL - 1.0) / 2.0);
        float a = (2 * l + 1) * (l * (l + 1) - 3 * sigma2);
        a /= 6 * (sigma2 - (l + 1) * (l + 1));
        return l + a;
    }

    private static PilImage BoxBlur(PilImage imIn, float xradius, float yradius, int n)
    {
        var ps = imIn.PixelSize;
        var imOut = imIn.Copy();
        if (xradius != 0)
            for (var i = 0; i < n; i++)
                HorizontalBoxBlur(imOut.Data, imOut.Width, imOut.Height, ps, xradius);
        if (yradius != 0)
        {
            var t = Transpose(imOut.Data, imOut.Width, imOut.Height, ps);
            for (var i = 0; i < n; i++)
                HorizontalBoxBlur(t, imOut.Height, imOut.Width, ps, yradius);
            var back = Transpose(t, imOut.Height, imOut.Width, ps);
            Buffer.BlockCopy(back, 0, imOut.Data, 0, back.Length);
        }

        return imOut;
    }

    private static byte[] Transpose(byte[] data, int w, int h, int ps)
    {
        var o = new byte[data.Length];
        for (var y = 0; y < h; y++)
            for (var x = 0; x < w; x++)
                for (var c = 0; c < ps; c++)
                    o[(x * h + y) * ps + c] = data[(y * w + x) * ps + c];
        return o;
    }

    private static void HorizontalBoxBlur(byte[] data, int w, int h, int ps, float floatRadius)
    {
        var radius = (int)floatRadius;
        var ww = (uint)((float)(1 << 24) / (floatRadius * 2 + 1));
        var fw = (uint)(((1 << 24) - (radius * 2 + 1) * ww) / 2);
        var edgeA = Math.Min(radius + 1, w);
        var edgeB = Math.Max(w - radius - 1, 0);
        var line = new byte[w * ps];
        var lastx = w - 1;
        for (var y = 0; y < h; y++)
        {
            var row = y * w * ps;
            for (var c = 0; c < ps; c++)
            {
                uint In(int x) => data[row + x * ps + c];
                void Save(int x, uint bulk) => line[x * ps + c] = (byte)((bulk + (1u << 23)) >> 24);

                var acc = In(0) * (uint)(radius + 1);
                for (var x = 0; x < edgeA - 1; x++)
                    acc += In(x);
                acc += In(lastx) * (uint)(radius - edgeA + 1);

                if (edgeA <= edgeB)
                {
                    for (var x = 0; x < edgeA; x++)
                    {
                        acc += In(x + radius) - In(0);
                        Save(x, acc * ww + (In(0) + In(x + radius + 1)) * fw);
                    }

                    for (var x = edgeA; x < edgeB; x++)
                    {
                        acc += In(x + radius) - In(x - radius - 1);
                        Save(x, acc * ww + (In(x - radius - 1) + In(x + radius + 1)) * fw);
                    }

                    for (var x = edgeB; x <= lastx; x++)
                    {
                        acc += In(lastx) - In(x - radius - 1);
                        Save(x, acc * ww + (In(x - radius - 1) + In(lastx)) * fw);
                    }
                }
                else
                {
                    for (var x = 0; x < edgeB; x++)
                    {
                        acc += In(x + radius) - In(0);
                        Save(x, acc * ww + (In(0) + In(x + radius + 1)) * fw);
                    }

                    for (var x = edgeB; x < edgeA; x++)
                    {
                        acc += In(lastx) - In(0);
                        Save(x, acc * ww + (In(0) + In(lastx)) * fw);
                    }

                    for (var x = edgeA; x <= lastx; x++)
                    {
                        acc += In(lastx) - In(x - radius - 1);
                        Save(x, acc * ww + (In(x - radius - 1) + In(lastx)) * fw);
                    }
                }
            }

            Buffer.BlockCopy(line, 0, data, row, line.Length);
        }
    }

    // ------------------------------------------------------------------ Geometry.c (affine, bilinear / bicubic)

    /// <summary>
    /// <c>Image.rotate(angle, resample, center=center, translate=translate)</c> without expand: counter-clockwise
    /// degrees, the output keeps the size, the outside is transparent black. RGBA is rotated premultiplied.
    /// </summary>
    public static PilImage Rotate(PilImage im, double angle, Resampling resampling,
        (double X, double Y)? center = null, (double X, double Y)? translate = null)
    {
        angle %= 360.0;
        if (angle < 0)
            angle += 360.0;
        var w = im.Width;
        var h = im.Height;
        var post = translate ?? (0, 0);
        var c = center ?? (w / 2.0, h / 2.0);
        var rad = -(angle * (Math.PI / 180.0));   // math.radians multiplies by the constant pi / 180
        double[] m =
        [
            Round15(Math.Cos(rad)), Round15(Math.Sin(rad)), 0.0,
            Round15(-Math.Sin(rad)), Round15(Math.Cos(rad)), 0.0
        ];
        var tx = -c.X - post.X;
        var ty = -c.Y - post.Y;
        var m2 = m[0] * tx + m[1] * ty + m[2];
        var m5 = m[3] * tx + m[4] * ty + m[5];
        m[2] = m2 + c.X;
        m[5] = m5 + c.Y;
        if (im.Mode == PilMode.RGBA)
            return Affine(im.Convert(PilMode.RGBa), m, resampling).Convert(PilMode.RGBA);
        return Affine(im, m, resampling);
    }

    /// <summary>Python's <c>round(x, 15)</c>: the correctly rounded 15-decimal value.</summary>
    private static double Round15(double x) =>
        double.Parse(x.ToString("F15", System.Globalization.CultureInfo.InvariantCulture),
            System.Globalization.CultureInfo.InvariantCulture);

    private static int Floor(double v) => v < 0.0 ? (int)Math.Floor(v) : (int)v;

    private static PilImage Affine(PilImage imIn, double[] a, Resampling resampling)
    {
        if (resampling is not (Resampling.Bilinear or Resampling.Bicubic))
            throw new NotSupportedException($"Affine {resampling} is not ported.");
        var o = new PilImage(imIn.Mode, imIn.Width, imIn.Height);
        var ps = imIn.PixelSize;
        var bands = BandsOf(imIn.Mode);
        Span<byte> px = stackalloc byte[4];
        for (var y = 0; y < o.Height; y++)
        {
            for (var x = 0; x < o.Width; x++)
            {
                var xin = x + 0.5;
                var yin = y + 0.5;
                var xx = a[0] * xin + a[1] * yin + a[2];
                var yy = a[3] * xin + a[4] * yin + a[5];
                var ok = resampling == Resampling.Bilinear
                    ? BilinearSample(imIn, xx, yy, bands, px)
                    : BicubicSample(imIn, xx, yy, bands, px);
                var di = (y * o.Width + x) * ps;
                for (var c = 0; c < ps; c++)
                    o.Data[di + c] = ok ? px[c] : (byte)0;
            }
        }

        return o;
    }

    private static int XClip(PilImage im, int x) => x < 0 ? 0 : x < im.Width ? x : im.Width - 1;
    private static int YClip(PilImage im, int y) => y < 0 ? 0 : y < im.Height ? y : im.Height - 1;

    private static bool BilinearSample(PilImage im, double xin, double yin, int bands, Span<byte> outPx)
    {
        if (xin < 0.0 || xin >= im.Width || yin < 0.0 || yin >= im.Height)
            return false;
        xin -= 0.5;
        yin -= 0.5;
        var x = Floor(xin);
        var y = Floor(yin);
        var dx = xin - x;
        var dy = yin - y;
        var ps = im.PixelSize;
        for (var b = 0; b < bands; b++)
        {
            double In(int row, int col) => im.Data[(row * im.Width + col) * ps + b];
            var x0 = XClip(im, x);
            var x1 = XClip(im, x + 1);
            var row0 = YClip(im, y);
            var v1 = In(row0, x0) + (In(row0, x1) - In(row0, x0)) * dx;
            double v2;
            if (y + 1 >= 0 && y + 1 < im.Height)
                v2 = In(y + 1, x0) + (In(y + 1, x1) - In(y + 1, x0)) * dx;
            else
                v2 = v1;
            v1 = v1 + (v2 - v1) * dy;
            outPx[b] = (byte)v1;
        }

        if (bands == 3 && ps == 4)
            outPx[3] = 0;
        return true;
    }

    private static double Cubic(double v1, double v2, double v3, double v4, double d)
    {
        var p1 = v2;
        var p2 = -v1 + v3;
        var p3 = 2 * (v1 - v2) + v3 - v4;
        var p4 = -v1 + v2 - v3 + v4;
        return p1 + d * (p2 + d * (p3 + d * p4));
    }

    private static bool BicubicSample(PilImage im, double xin, double yin, int bands, Span<byte> outPx)
    {
        if (xin < 0.0 || xin >= im.Width || yin < 0.0 || yin >= im.Height)
            return false;
        xin -= 0.5;
        yin -= 0.5;
        var x = Floor(xin);
        var y = Floor(yin);
        var dx = xin - x;
        var dy = yin - y;
        x--;
        y--;
        var ps = im.PixelSize;
        var x0 = XClip(im, x);
        var x1 = XClip(im, x + 1);
        var x2 = XClip(im, x + 2);
        var x3 = XClip(im, x + 3);
        for (var b = 0; b < bands; b++)
        {
            double Row(int row)
            {
                var o = row * im.Width;
                return Cubic(im.Data[(o + x0) * ps + b], im.Data[(o + x1) * ps + b],
                    im.Data[(o + x2) * ps + b], im.Data[(o + x3) * ps + b], dx);
            }

            var v1 = Row(YClip(im, y));
            var v2 = y + 1 >= 0 && y + 1 < im.Height ? Row(y + 1) : v1;
            var v3 = y + 2 >= 0 && y + 2 < im.Height ? Row(y + 2) : v2;
            var v4 = y + 3 >= 0 && y + 3 < im.Height ? Row(y + 3) : v3;
            var v = Cubic(v1, v2, v3, v4, dy);
            outPx[b] = v <= 0.0 ? (byte)0 : v >= 255.0 ? (byte)255 : (byte)v;
        }

        if (bands == 3 && ps == 4)
            outPx[3] = 0;
        return true;
    }
}
