namespace WindrunnerLauncher.Core.Tests;

public class SidecarFileTests
{
    [Fact]
    public void Ensure_WritesMissingFile()
    {
        using var tmp = new TempDir();
        var dest = tmp.Combine("libSkiaSharp.dll");
        var payload = new byte[] { 1, 2, 3, 4 };

        Assert.True(SidecarFile.Ensure(dest, payload));
        Assert.Equal(payload, File.ReadAllBytes(dest));
    }

    [Fact]
    public void Ensure_SkipsWhenLengthMatches()
    {
        using var tmp = new TempDir();
        var dest = tmp.Combine("libHarfBuzzSharp.dll");
        File.WriteAllBytes(dest, [9, 9, 9]);

        Assert.False(SidecarFile.Ensure(dest, [1, 2, 3]));
        Assert.Equal(new byte[] { 9, 9, 9 }, File.ReadAllBytes(dest));
    }

    [Fact]
    public void Ensure_ReplacesWhenLengthDiffers()
    {
        using var tmp = new TempDir();
        var dest = tmp.Combine("av_libglesv2.dll");
        File.WriteAllBytes(dest, [1]);

        var updated = new byte[] { 2, 2, 2, 2 };
        Assert.True(SidecarFile.Ensure(dest, updated));
        Assert.Equal(updated, File.ReadAllBytes(dest));
    }

    [Fact]
    public void Ensure_CreatesParentDirectory()
    {
        using var tmp = new TempDir();
        var dest = tmp.Combine("nested", "out", "native.dll");

        Assert.True(SidecarFile.Ensure(dest, [7, 8]));
        Assert.True(File.Exists(dest));
    }
}
