namespace WindrunnerLauncher.Core.Hearthstone.Imaging;

public enum PilMode
{
    /// <summary>8-bit grey, one byte per pixel.</summary>
    L,

    /// <summary>Colour, four bytes per pixel; the fourth byte is padding (255).</summary>
    RGB,

    /// <summary>Colour with straight alpha.</summary>
    RGBA,

    /// <summary>Colour premultiplied by alpha.</summary>
    RGBa
}

public enum Resampling
{
    Nearest,
    Box,
    Bilinear,
    Bicubic,
    Lanczos
}

/// <summary>
/// An in-memory image with the storage layout and the arithmetic of Pillow 12, so the art pipeline of
/// the Hearthstone module (tools/art, written against Pillow) produces the same bytes when ported.
/// Every operation here is a port of the matching libImaging routine; the comments name it.
/// </summary>
public sealed class PilImage
{
    public PilMode Mode { get; }
    public int Width { get; }
    public int Height { get; }

    /// <summary>Row-major pixels: one byte per pixel for L, four (R, G, B, A/pad) otherwise.</summary>
    public byte[] Data { get; }

    public int PixelSize => Mode == PilMode.L ? 1 : 4;

    public PilImage(PilMode mode, int width, int height, byte[]? data = null)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(width);
        ArgumentOutOfRangeException.ThrowIfNegative(height);
        Mode = mode;
        Width = width;
        Height = height;
        var size = width * height * (mode == PilMode.L ? 1 : 4);
        if (data is not null && data.Length != size)
            throw new ArgumentException($"Expected {size} bytes for a {width}x{height} {mode} image, got {data.Length}.");
        Data = data ?? new byte[size];
    }

    /// <summary><c>Image.new(mode, size, color)</c>. RGB images get 255 in the padding byte.</summary>
    public static PilImage New(PilMode mode, int width, int height, uint rgba = 0)
    {
        var im = new PilImage(mode, width, height);
        if (mode == PilMode.L)
        {
            if (rgba != 0)
                Array.Fill(im.Data, (byte)rgba);
            return im;
        }

        var r = (byte)(rgba >> 24);
        var g = (byte)(rgba >> 16);
        var b = (byte)(rgba >> 8);
        var a = mode == PilMode.RGB ? (byte)255 : (byte)rgba;
        if (r == 0 && g == 0 && b == 0 && a == 0)
            return im;
        for (var i = 0; i < im.Data.Length; i += 4)
        {
            im.Data[i] = r;
            im.Data[i + 1] = g;
            im.Data[i + 2] = b;
            im.Data[i + 3] = a;
        }

        return im;
    }

    public static uint Rgba(int r, int g, int b, int a = 255) =>
        ((uint)(byte)r << 24) | ((uint)(byte)g << 16) | ((uint)(byte)b << 8) | (byte)a;

    public PilImage Copy() => new(Mode, Width, Height, (byte[])Data.Clone());

    public int Offset(int x, int y) => (y * Width + x) * PixelSize;

    public byte this[int x, int y]
    {
        get => Data[y * Width + x];
        set => Data[y * Width + x] = value;
    }

    public (byte R, byte G, byte B, byte A) GetRgba(int x, int y)
    {
        var o = (y * Width + x) * 4;
        return (Data[o], Data[o + 1], Data[o + 2], Data[o + 3]);
    }

    public void SetRgba(int x, int y, int r, int g, int b, int a)
    {
        var o = (y * Width + x) * 4;
        Data[o] = (byte)r;
        Data[o + 1] = (byte)g;
        Data[o + 2] = (byte)b;
        Data[o + 3] = (byte)a;
    }

    // ------------------------------------------------------------------ conversions (Convert.c)

    /// <summary><c>Image.convert(mode)</c> for the conversions the pipeline uses.</summary>
    public PilImage Convert(PilMode mode)
    {
        if (mode == Mode)
            return Copy();
        var o = new PilImage(mode, Width, Height);
        var n = Width * Height;
        var s = Data;
        var d = o.Data;
        switch (Mode, mode)
        {
            case (PilMode.L, PilMode.RGB):
            case (PilMode.L, PilMode.RGBA):
                for (var i = 0; i < n; i++)
                {
                    d[i * 4] = d[i * 4 + 1] = d[i * 4 + 2] = s[i];
                    d[i * 4 + 3] = 255;
                }
                break;
            case (PilMode.RGB, PilMode.L):
            case (PilMode.RGBA, PilMode.L):
                // rgb2l: ITU-R 601-2 luma, L24
                for (var i = 0; i < n; i++)
                    d[i] = (byte)((s[i * 4] * 19595 + s[i * 4 + 1] * 38470 + s[i * 4 + 2] * 7471 + 0x8000) >> 16);
                break;
            case (PilMode.RGB, PilMode.RGBA):
                Buffer.BlockCopy(s, 0, d, 0, s.Length);
                for (var i = 0; i < n; i++)
                    d[i * 4 + 3] = 255;
                break;
            case (PilMode.RGBA, PilMode.RGB):
                Buffer.BlockCopy(s, 0, d, 0, s.Length);
                for (var i = 0; i < n; i++)
                    d[i * 4 + 3] = 255;
                break;
            case (PilMode.RGBA, PilMode.RGBa):
                // rgbA2rgba
                for (var i = 0; i < n * 4; i += 4)
                {
                    uint a = s[i + 3];
                    d[i] = MulDiv255(s[i], a);
                    d[i + 1] = MulDiv255(s[i + 1], a);
                    d[i + 2] = MulDiv255(s[i + 2], a);
                    d[i + 3] = (byte)a;
                }
                break;
            case (PilMode.RGBa, PilMode.RGBA):
                // rgba2rgbA
                for (var i = 0; i < n * 4; i += 4)
                {
                    int a = s[i + 3];
                    if (a == 255 || a == 0)
                    {
                        d[i] = s[i];
                        d[i + 1] = s[i + 1];
                        d[i + 2] = s[i + 2];
                    }
                    else
                    {
                        d[i] = Clip8(255 * s[i] / a);
                        d[i + 1] = Clip8(255 * s[i + 1] / a);
                        d[i + 2] = Clip8(255 * s[i + 2] / a);
                    }

                    d[i + 3] = (byte)a;
                }
                break;
            case (PilMode.RGBa, PilMode.RGB):
                return Convert(PilMode.RGBA).Convert(PilMode.RGB);
            default:
                throw new NotSupportedException($"Conversion {Mode} -> {mode} is not ported.");
        }

        return o;
    }

    internal static byte MulDiv255(uint a, uint b)
    {
        var tmp = a * b + 128;
        return (byte)(((tmp >> 8) + tmp) >> 8);
    }

    internal static byte Clip8(int v) => v <= 0 ? (byte)0 : v < 256 ? (byte)v : (byte)255;

    /// <summary><c>BLEND(mask, in1, in2)</c> of ImagingUtils.h.</summary>
    internal static byte Blend(uint mask, uint in1, uint in2)
    {
        var tmp = in1 * (255 - mask) + in2 * mask + 128;
        return (byte)(((tmp >> 8) + tmp) >> 8);
    }

    // ------------------------------------------------------------------ bands

    /// <summary>One channel as an L image (<c>Image.getchannel</c> / <c>split()[i]</c>).</summary>
    public PilImage Band(int index)
    {
        if (Mode == PilMode.L)
            return Copy();
        var o = new PilImage(PilMode.L, Width, Height);
        for (var i = 0; i < o.Data.Length; i++)
            o.Data[i] = Data[i * 4 + index];
        return o;
    }

    /// <summary><c>Image.putalpha(mask)</c> with an L mask of the same size.</summary>
    public void PutAlpha(PilImage alpha)
    {
        RequireMode(PilMode.RGBA);
        if (alpha.Mode != PilMode.L || alpha.Width != Width || alpha.Height != Height)
            throw new ArgumentException("putalpha needs an L image of the same size.");
        for (var i = 0; i < alpha.Data.Length; i++)
            Data[i * 4 + 3] = alpha.Data[i];
    }

    /// <summary>
    /// <c>Image.merge("RGBA", (chops.multiply(r, a), chops.multiply(g, a), chops.multiply(b, a), a))</c>:
    /// every colour channel times alpha / 255, truncated (Chops.c multiply), kept in mode RGBA.
    /// </summary>
    public PilImage MultiplyByAlpha()
    {
        RequireMode(PilMode.RGBA);
        var o = new PilImage(PilMode.RGBA, Width, Height);
        for (var i = 0; i < Data.Length; i += 4)
        {
            int a = Data[i + 3];
            o.Data[i] = (byte)(Data[i] * a / 255);
            o.Data[i + 1] = (byte)(Data[i + 1] * a / 255);
            o.Data[i + 2] = (byte)(Data[i + 2] * a / 255);
            o.Data[i + 3] = (byte)a;
        }

        return o;
    }

    // ------------------------------------------------------------------ geometry helpers

    /// <summary><c>Image.crop(box)</c>: integer box, areas outside the image are zero.</summary>
    public PilImage Crop(int x0, int y0, int x1, int y1)
    {
        var w = Math.Max(0, x1 - x0);
        var h = Math.Max(0, y1 - y0);
        var o = new PilImage(Mode, w, h);
        var ps = PixelSize;
        for (var y = 0; y < h; y++)
        {
            var sy = y + y0;
            if (sy < 0 || sy >= Height)
                continue;
            var sx0 = Math.Max(0, x0);
            var sx1 = Math.Min(Width, x1);
            if (sx1 <= sx0)
                continue;
            Buffer.BlockCopy(Data, (sy * Width + sx0) * ps, o.Data, (y * w + (sx0 - x0)) * ps, (sx1 - sx0) * ps);
        }

        return o;
    }

    /// <summary><c>Image.transpose(FLIP_TOP_BOTTOM)</c>.</summary>
    public PilImage FlipTopBottom()
    {
        var o = new PilImage(Mode, Width, Height);
        var row = Width * PixelSize;
        for (var y = 0; y < Height; y++)
            Buffer.BlockCopy(Data, y * row, o.Data, (Height - 1 - y) * row, row);
        return o;
    }

    /// <summary>
    /// <c>Image.paste(im, (x, y), mask)</c>: a straight copy, or a blend through an L mask or through the
    /// alpha channel of an RGBA mask (Paste.c paste / paste_mask_L / paste_mask_RGBA). Clipped to the image.
    /// </summary>
    public void Paste(PilImage src, int x, int y, PilImage? mask = null)
    {
        if (src.Mode != Mode)
        {
            if (Mode == PilMode.RGB && src.Mode is PilMode.RGBA or PilMode.RGBa)
            {
                // Pillow pastes RGBA into RGB without converting it (the fourth byte is padding)
            }
            else
            {
                src = src.Convert(Mode);
            }
        }

        if (mask is not null && (mask.Width != src.Width || mask.Height != src.Height))
            throw new ArgumentException("The mask must have the size of the pasted image.");

        ClipPaste(x, y, src.Width, src.Height, out var dx, out var dy, out var sx, out var sy, out var w, out var h);
        if (w <= 0 || h <= 0)
            return;
        var ps = PixelSize;
        for (var row = 0; row < h; row++)
        {
            var di = ((dy + row) * Width + dx) * ps;
            var si = ((sy + row) * src.Width + sx) * ps;
            if (mask is null)
            {
                Buffer.BlockCopy(src.Data, si, Data, di, w * ps);
                continue;
            }

            for (var col = 0; col < w; col++)
            {
                uint m = mask.Mode == PilMode.L
                    ? mask.Data[(sy + row) * mask.Width + sx + col]
                    : mask.Data[((sy + row) * mask.Width + sx + col) * 4 + 3];
                for (var c = 0; c < ps; c++)
                    Data[di + col * ps + c] = Blend(m, Data[di + col * ps + c], src.Data[si + col * ps + c]);
            }
        }
    }

    /// <summary>
    /// <c>Image.paste(color, box, mask)</c> with a solid colour: a plain fill, or a blend through an L mask
    /// the size of the box (Paste.c fill / fill_mask_L). <paramref name="box"/> null is the whole image.
    /// </summary>
    public void PasteColor(uint value, (int X0, int Y0, int X1, int Y1)? box = null, PilImage? mask = null)
    {
        var (bx0, by0, bx1, by1) = box ?? (0, 0, mask?.Width ?? Width, mask?.Height ?? Height);
        Span<byte> ink = stackalloc byte[4];
        if (Mode == PilMode.L)
        {
            ink[0] = (byte)value;
        }
        else
        {
            ink[0] = (byte)(value >> 24);
            ink[1] = (byte)(value >> 16);
            ink[2] = (byte)(value >> 8);
            ink[3] = Mode == PilMode.RGB ? (byte)255 : (byte)value;
        }

        ClipPaste(bx0, by0, bx1 - bx0, by1 - by0, out var dx, out var dy, out var sx, out var sy, out var w, out var h);
        var ps = PixelSize;
        for (var row = 0; row < h; row++)
        {
            for (var col = 0; col < w; col++)
            {
                var di = ((dy + row) * Width + dx + col) * ps;
                if (mask is null)
                {
                    for (var c = 0; c < ps; c++)
                        Data[di + c] = ink[c];
                    continue;
                }

                uint m = mask.Data[(sy + row) * mask.Width + sx + col];
                for (var c = 0; c < ps; c++)
                    Data[di + c] = Blend(m, Data[di + c], ink[c]);
            }
        }
    }

    private void ClipPaste(int x, int y, int sw, int sh, out int dx, out int dy, out int sx, out int sy, out int w, out int h)
    {
        dx = x;
        dy = y;
        sx = 0;
        sy = 0;
        w = sw;
        h = sh;
        if (dx < 0)
        {
            sx = -dx;
            w += dx;
            dx = 0;
        }

        if (dy < 0)
        {
            sy = -dy;
            h += dy;
            dy = 0;
        }

        if (dx + w > Width)
            w = Width - dx;
        if (dy + h > Height)
            h = Height - dy;
    }

    /// <summary><c>Image.composite(image1, image2, mask)</c>: image2 with image1 pasted through the mask.</summary>
    public static PilImage Composite(PilImage image1, PilImage image2, PilImage mask)
    {
        var o = image2.Copy();
        o.Paste(image1, 0, 0, mask);
        return o;
    }

    /// <summary><c>Image.alpha_composite(im, dest)</c> (AlphaComposite.c over the destination box).</summary>
    public void AlphaComposite(PilImage src, int destX = 0, int destY = 0)
    {
        RequireMode(PilMode.RGBA);
        if (src.Mode != PilMode.RGBA)
            throw new ArgumentException("alpha_composite needs RGBA images.");
        var background = destX == 0 && destY == 0 && src.Width == Width && src.Height == Height
            ? this
            : Crop(destX, destY, destX + src.Width, destY + src.Height);
        var result = new PilImage(PilMode.RGBA, src.Width, src.Height);
        const int precisionBits = 7;
        var dst = background.Data;
        var s = src.Data;
        var o = result.Data;
        for (var i = 0; i < o.Length; i += 4)
        {
            uint sa = s[i + 3];
            if (sa == 0)
            {
                o[i] = dst[i];
                o[i + 1] = dst[i + 1];
                o[i + 2] = dst[i + 2];
                o[i + 3] = dst[i + 3];
                continue;
            }

            uint blend = dst[i + 3] * (255 - sa);
            uint outa255 = sa * 255 + blend;
            uint coef1 = sa * 255 * 255 * (1u << precisionBits) / outa255;
            uint coef2 = 255 * (1u << precisionBits) - coef1;
            for (var c = 0; c < 3; c++)
            {
                uint tmp = s[i + c] * coef1 + dst[i + c] * coef2 + (0x80u << precisionBits);
                o[i + c] = (byte)((((tmp >> 8) + tmp) >> 8) >> precisionBits);
            }

            uint ta = outa255 + 0x80;
            o[i + 3] = (byte)(((ta >> 8) + ta) >> 8);
        }

        Paste(result, destX, destY);
    }

    // ------------------------------------------------------------------ queries

    /// <summary>
    /// <c>Image.getbbox()</c>: the bounding box (exclusive right/bottom) of non-zero pixels, of the alpha
    /// channel for RGBA, or null when everything is zero (GetBBox.c).
    /// </summary>
    public (int X0, int Y0, int X1, int Y1)? GetBBox()
    {
        int x0 = Width, y0 = Height, x1 = 0, y1 = 0;
        var found = false;
        for (var y = 0; y < Height; y++)
        {
            for (var x = 0; x < Width; x++)
            {
                var v = Mode switch
                {
                    PilMode.L => Data[y * Width + x],
                    PilMode.RGBA or PilMode.RGBa => Data[(y * Width + x) * 4 + 3],
                    _ => (Data[(y * Width + x) * 4] | Data[(y * Width + x) * 4 + 1] | Data[(y * Width + x) * 4 + 2])
                };
                if (v == 0)
                    continue;
                found = true;
                if (x < x0) x0 = x;
                if (x >= x1) x1 = x + 1;
                if (y < y0) y0 = y;
                y1 = y + 1;
            }
        }

        return found ? (x0, y0, x1, y1) : null;
    }

    /// <summary><c>Image.point(lut)</c> for an L image.</summary>
    public PilImage Point(Func<int, int> lut)
    {
        RequireMode(PilMode.L);
        var table = new byte[256];
        for (var i = 0; i < 256; i++)
            table[i] = Clip8(lut(i));
        var o = new PilImage(PilMode.L, Width, Height);
        for (var i = 0; i < Data.Length; i++)
            o.Data[i] = table[Data[i]];
        return o;
    }

    /// <summary><c>ImageDraw.floodfill(image, xy, value)</c> on an L image (4-neighbour, exact match).</summary>
    public void FloodFill(int x, int y, byte value)
    {
        RequireMode(PilMode.L);
        if (x < 0 || y < 0 || x >= Width || y >= Height)
            return;
        var background = this[x, y];
        if (background == value)
            return;
        this[x, y] = value;
        var stack = new Stack<(int X, int Y)>();
        stack.Push((x, y));
        while (stack.Count > 0)
        {
            var (px, py) = stack.Pop();
            Visit(px + 1, py);
            Visit(px - 1, py);
            Visit(px, py + 1);
            Visit(px, py - 1);
        }

        void Visit(int sx, int sy)
        {
            if (sx < 0 || sy < 0 || sx >= Width || sy >= Height || this[sx, sy] != background)
                return;
            this[sx, sy] = value;
            stack.Push((sx, sy));
        }
    }

    /// <summary><c>ImageDraw.rectangle((x0, y0, x1, y1), fill)</c> on an L image: inclusive corners.</summary>
    public void FillRectangle(int x0, int y0, int x1, int y1, byte value)
    {
        RequireMode(PilMode.L);
        for (var y = Math.Max(0, y0); y <= Math.Min(Height - 1, y1); y++)
            for (var x = Math.Max(0, x0); x <= Math.Min(Width - 1, x1); x++)
                this[x, y] = value;
    }

    internal void RequireMode(PilMode mode)
    {
        if (Mode != mode)
            throw new InvalidOperationException($"Expected a {mode} image, got {Mode}.");
    }
}
