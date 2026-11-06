using SkiaSharp;

namespace WindrunnerLauncher.Core.Hearthstone.Imaging;

/// <summary>
/// PNG and JPEG decoding into straight-alpha RGBA. No colour management is applied (Pillow ignores
/// embedded colour profiles and gamma too), so decoded pixels are the file's own values.
/// </summary>
public static class ImageCodec
{
    /// <summary><c>Image.open(path).convert("RGBA")</c>.</summary>
    public static PilImage LoadRgba(string path) => DecodeRgba(File.ReadAllBytes(path), path);

    public static PilImage DecodeRgba(byte[] bytes, string? name = null)
    {
        using var data = SKData.CreateCopy(bytes);
        using var codec = SKCodec.Create(data)
                          ?? throw new InvalidDataException($"{name ?? "image"} is not a PNG or JPEG file.");
        var info = new SKImageInfo(codec.Info.Width, codec.Info.Height, SKColorType.Rgba8888, SKAlphaType.Unpremul);
        var image = new PilImage(PilMode.RGBA, info.Width, info.Height);
        var handle = System.Runtime.InteropServices.GCHandle.Alloc(image.Data, System.Runtime.InteropServices.GCHandleType.Pinned);
        try
        {
            var result = codec.GetPixels(info, handle.AddrOfPinnedObject());
            if (result is not (SKCodecResult.Success or SKCodecResult.IncompleteInput))
                throw new InvalidDataException($"{name ?? "image"} could not be decoded ({result}).");
        }
        finally
        {
            handle.Free();
        }

        return image;
    }

    /// <summary>Width and height from a PNG header, without decoding (fetch_renders.png_info).</summary>
    public static (int Width, int Height)? PngSize(ReadOnlySpan<byte> data)
    {
        ReadOnlySpan<byte> magic = [0x89, (byte)'P', (byte)'N', (byte)'G', 0x0D, 0x0A, 0x1A, 0x0A];
        if (data.Length < 24 || !data[..8].SequenceEqual(magic))
            return null;
        return (System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(data[16..20]),
                System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(data[20..24]));
    }

    /// <summary>Writes an RGBA image as PNG (review output and tests only; textures are BLP).</summary>
    public static void SavePng(PilImage image, string path)
    {
        var rgba = image.Mode switch
        {
            PilMode.RGBA => image,
            PilMode.L => image.Convert(PilMode.RGBA),
            _ => image.Convert(PilMode.RGBA)
        };
        var info = new SKImageInfo(rgba.Width, rgba.Height, SKColorType.Rgba8888, SKAlphaType.Unpremul);
        using var bitmap = new SKBitmap();
        var handle = System.Runtime.InteropServices.GCHandle.Alloc(rgba.Data, System.Runtime.InteropServices.GCHandleType.Pinned);
        try
        {
            bitmap.InstallPixels(info, handle.AddrOfPinnedObject(), info.RowBytes);
            using var encoded = bitmap.Encode(SKEncodedImageFormat.Png, 100);
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
            File.WriteAllBytes(path, encoded.ToArray());
        }
        finally
        {
            handle.Free();
        }
    }
}
