using System.Buffers.Binary;

namespace WindrunnerLauncher.Core.Hearthstone.Imaging;

/// <summary>
/// BLP2 textures (DXT5, alpha depth 8, full mip chain) for the 1.12 client, a port of the module's
/// tools/art/convert_blp.py. The encoder is that script's own (not a library's), so the output matches it.
/// </summary>
public static class BlpWriter
{
    private const int HeaderSize = 148 + 1024;   // BLP2 header + the (unused for DXT) 256 entry palette

    /// <summary>The BLP file and the number of mip levels.</summary>
    public static (byte[] Blp, int Levels) Build(PilImage canvas)
    {
        canvas.RequireMode(PilMode.RGBA);
        var levels = MipChain(canvas);
        var datas = levels.Select(EncodeLevel).ToList();
        var total = HeaderSize + datas.Sum(d => d.Length);
        var blp = new byte[total];
        var span = blp.AsSpan();
        "BLP2"u8.CopyTo(span);
        BinaryPrimitives.WriteUInt32LittleEndian(span[4..], 1);   // type 1
        span[8] = 2;    // DXT compression
        span[9] = 8;    // alpha depth
        span[10] = 7;   // alpha encoding: DXT5
        span[11] = 1;   // has mips
        BinaryPrimitives.WriteUInt32LittleEndian(span[12..], (uint)canvas.Width);
        BinaryPrimitives.WriteUInt32LittleEndian(span[16..], (uint)canvas.Height);
        var pos = HeaderSize;
        for (var i = 0; i < datas.Count; i++)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(span[(20 + i * 4)..], (uint)pos);
            BinaryPrimitives.WriteUInt32LittleEndian(span[(84 + i * 4)..], (uint)datas[i].Length);
            datas[i].CopyTo(span[pos..]);
            pos += datas[i].Length;
        }

        return (blp, levels.Count);
    }

    /// <summary>
    /// The mip chain of convert_blp.mip_chain: each level is the previous one with its colour multiplied by
    /// alpha, box-halved (Pillow premultiplies RGBA once more for the resize) and divided by alpha again.
    /// </summary>
    public static List<PilImage> MipChain(PilImage image)
    {
        var levels = new List<PilImage> { image };
        var cur = image;
        while (cur.Width > 1 || cur.Height > 1)
        {
            var w = Math.Max(1, cur.Width / 2);
            var h = Math.Max(1, cur.Height / 2);
            var pm = PilOps.Resize(cur.MultiplyByAlpha(), w, h, Resampling.Box);
            levels.Add(Unpremultiply(pm));
            cur = levels[^1];
        }

        return levels;
    }

    /// <summary>The per-pixel division of convert_blp.mip_chain and cardimg.box_down: min(255, int(c * 255 / a + .5)).</summary>
    public static PilImage Unpremultiply(PilImage pm)
    {
        var o = new PilImage(PilMode.RGBA, pm.Width, pm.Height);
        for (var i = 0; i < pm.Data.Length; i += 4)
        {
            int a = pm.Data[i + 3];
            if (a == 0)
                continue;
            var k = 255.0 / a;
            o.Data[i] = (byte)Math.Min(255, (int)(pm.Data[i] * k + .5));
            o.Data[i + 1] = (byte)Math.Min(255, (int)(pm.Data[i + 1] * k + .5));
            o.Data[i + 2] = (byte)Math.Min(255, (int)(pm.Data[i + 2] * k + .5));
            o.Data[i + 3] = (byte)a;
        }

        return o;
    }

    private static byte[] EncodeLevel(PilImage im)
    {
        var w = im.Width;
        var h = im.Height;
        var bw = (Math.Max(w, 4) + 3) / 4;
        var bh = (Math.Max(h, 4) + 3) / 4;
        var outBytes = new byte[bw * bh * 16];
        var block = new int[64];
        var o = 0;
        for (var by = 0; by < Math.Max(h, 4); by += 4)
        {
            for (var bx = 0; bx < Math.Max(w, 4); bx += 4)
            {
                for (var y = 0; y < 4; y++)
                {
                    var yy = Math.Min(by + y, h - 1);
                    for (var x = 0; x < 4; x++)
                    {
                        var xx = Math.Min(bx + x, w - 1);
                        var s = (yy * w + xx) * 4;
                        var d = (y * 4 + x) * 4;
                        block[d] = im.Data[s];
                        block[d + 1] = im.Data[s + 1];
                        block[d + 2] = im.Data[s + 2];
                        block[d + 3] = im.Data[s + 3];
                    }
                }

                EncodeBlock(block, outBytes.AsSpan(o, 16));
                o += 16;
            }
        }

        return outBytes;
    }

    private static int C565(int r, int g, int b) => ((r >> 3) << 11) | ((g >> 2) << 5) | (b >> 3);

    private static (int R, int G, int B) From565(int v)
    {
        int r = (v >> 11) & 31, g = (v >> 5) & 63, b = v & 31;
        return ((r << 3) | (r >> 2), (g << 2) | (g >> 4), (b << 3) | (b >> 2));
    }

    /// <summary>convert_blp.encode_block: 16 RGBA pixels (row-major) to 16 bytes of DXT5.</summary>
    internal static void EncodeBlock(int[] px, Span<byte> dst)
    {
        dst.Clear();
        int amax = 0, amin = 255;
        for (var i = 0; i < 16; i++)
        {
            amax = Math.Max(amax, px[i * 4 + 3]);
            amin = Math.Min(amin, px[i * 4 + 3]);
        }

        if (amax == 0)
            return;

        dst[0] = (byte)amax;
        dst[1] = (byte)amin;
        if (amax != amin)
        {
            Span<int> levels = stackalloc int[8];
            levels[0] = amax;
            levels[1] = amin;
            for (var k = 2; k < 8; k++)
                levels[k] = ((8 - k) * amax + (k - 1) * amin) / 7;
            ulong bits = 0;
            for (var i = 0; i < 16; i++)
            {
                var a = px[i * 4 + 3];
                int best = 999, bi = 0;
                for (var k = 0; k < 8; k++)
                {
                    var d = Math.Abs(a - levels[k]);
                    if (d < best)
                    {
                        best = d;
                        bi = k;
                        if (d == 0)
                            break;
                    }
                }

                bits |= (ulong)bi << (3 * i);
            }

            for (var i = 0; i < 6; i++)
                dst[2 + i] = (byte)(bits >> (8 * i));
        }

        // colour block from the visible pixels
        int n = 0, sr = 0, sg = 0, sb = 0;
        int loR = 255, loG = 255, loB = 255, hiR = 0, hiG = 0, hiB = 0;
        for (var i = 0; i < 16; i++)
        {
            if (px[i * 4 + 3] <= 0)
                continue;
            int r = px[i * 4], g = px[i * 4 + 1], b = px[i * 4 + 2];
            n++;
            sr += r;
            sg += g;
            sb += b;
            loR = Math.Min(loR, r);
            loG = Math.Min(loG, g);
            loB = Math.Min(loB, b);
            hiR = Math.Max(hiR, r);
            hiG = Math.Max(hiG, g);
            hiB = Math.Max(hiB, b);
        }

        var mr = (double)sr / n;
        var mg = (double)sg / n;
        var mb = (double)sb / n;
        var colour = dst[8..];
        if (loR == hiR && loG == hiG && loB == hiB)
        {
            var c = C565(hiR, hiG, hiB);
            BinaryPrimitives.WriteUInt16LittleEndian(colour, (ushort)c);
            BinaryPrimitives.WriteUInt16LittleEndian(colour[2..], (ushort)c);
            return;
        }

        // principal axis by power iteration on the covariance matrix
        double cxx = 0, cxy = 0, cxz = 0, cyy = 0, cyz = 0, czz = 0;
        for (var i = 0; i < 16; i++)
        {
            if (px[i * 4 + 3] <= 0)
                continue;
            double dr = px[i * 4] - mr, dg = px[i * 4 + 1] - mg, db = px[i * 4 + 2] - mb;
            cxx += dr * dr;
            cxy += dr * dg;
            cxz += dr * db;
            cyy += dg * dg;
            cyz += dg * db;
            czz += db * db;
        }

        double ax0 = hiR - loR + 1.0, ax1 = hiG - loG + 0.5, ax2 = hiB - loB + 0.25;
        for (var it = 0; it < 6; it++)
        {
            var x = cxx * ax0 + cxy * ax1 + cxz * ax2;
            var y = cxy * ax0 + cyy * ax1 + cyz * ax2;
            var z = cxz * ax0 + cyz * ax1 + czz * ax2;
            var m = Math.Max(Math.Max(Math.Abs(x), Math.Abs(y)), Math.Abs(z));
            if (m == 0)
                break;
            ax0 = x / m;
            ax1 = y / m;
            ax2 = z / m;
        }

        double tmin = 1e9, tmax = -1e9;
        for (var i = 0; i < 16; i++)
        {
            if (px[i * 4 + 3] <= 0)
                continue;
            var t = (px[i * 4] - mr) * ax0 + (px[i * 4 + 1] - mg) * ax1 + (px[i * 4 + 2] - mb) * ax2;
            if (t < tmin)
                tmin = t;
            if (t > tmax)
                tmax = t;
        }

        var norm = ax0 * ax0 + ax1 * ax1 + ax2 * ax2;

        int Endpoint(double t)
        {
            var k = t / norm;
            static int Ch(double m, double k, double a) => Math.Max(0, Math.Min(255, (int)Math.Round(m + k * a, MidpointRounding.ToEven)));
            return C565(Ch(mr, k, ax0), Ch(mg, k, ax1), Ch(mb, k, ax2));
        }

        var c0 = Endpoint(tmax);
        var c1 = Endpoint(tmin);
        if (c0 < c1)
            (c0, c1) = (c1, c0);
        BinaryPrimitives.WriteUInt16LittleEndian(colour, (ushort)c0);
        BinaryPrimitives.WriteUInt16LittleEndian(colour[2..], (ushort)c1);
        if (c0 == c1)
            return;
        var q0 = From565(c0);
        var q1 = From565(c1);
        Span<int> pal = stackalloc int[12];
        pal[0] = q0.R; pal[1] = q0.G; pal[2] = q0.B;
        pal[3] = q1.R; pal[4] = q1.G; pal[5] = q1.B;
        pal[6] = (2 * q0.R + q1.R) / 3; pal[7] = (2 * q0.G + q1.G) / 3; pal[8] = (2 * q0.B + q1.B) / 3;
        pal[9] = (q0.R + 2 * q1.R) / 3; pal[10] = (q0.G + 2 * q1.G) / 3; pal[11] = (q0.B + 2 * q1.B) / 3;
        uint idx = 0;
        for (var i = 0; i < 16; i++)
        {
            int best = 1 << 30, bi = 0;
            for (var k = 0; k < 4; k++)
            {
                int dr = px[i * 4] - pal[k * 3], dg = px[i * 4 + 1] - pal[k * 3 + 1], db = px[i * 4 + 2] - pal[k * 3 + 2];
                var d = dr * dr + dg * dg + db * db;
                if (d < best)
                {
                    best = d;
                    bi = k;
                }
            }

            idx |= (uint)bi << (2 * i);
        }

        BinaryPrimitives.WriteUInt32LittleEndian(colour[4..], idx);
    }
}
