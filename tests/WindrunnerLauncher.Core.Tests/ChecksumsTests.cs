using WindrunnerLauncher.Core.Security;

namespace WindrunnerLauncher.Core.Tests;

public class ChecksumsTests
{
    [Fact]
    public void WowPassHash_AdminAdmin_MatchesKnownVector()
    {
        // SHA1("ADMIN:ADMIN") — the account hash MaNGOS/realmd expects for admin/admin.
        Assert.Equal("8301316d0d8448a34fa6d0c6bf1cbfa2b4a1a93a", Checksums.WowPassHash("admin", "admin"));
    }

    [Theory]
    [InlineData("admin", "admin")]
    [InlineData("Admin", "ADMIN")]
    [InlineData("ADMIN", "admin")]
    public void WowPassHash_IsCaseInsensitive(string user, string pass)
    {
        Assert.Equal("8301316d0d8448a34fa6d0c6bf1cbfa2b4a1a93a", Checksums.WowPassHash(user, pass));
    }

    [Fact]
    public void WowPassHash_IsLowercaseHex40()
    {
        var hash = Checksums.WowPassHash("someone", "secret");
        Assert.Equal(40, hash.Length);
        Assert.Equal(hash.ToLowerInvariant(), hash);
        Assert.Matches("^[0-9a-f]{40}$", hash);
    }

    [Fact]
    public void Sha256Hex_EmptyInput_MatchesKnownVector()
    {
        Assert.Equal(
            "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855",
            Checksums.Sha256Hex([]));
    }

    [Fact]
    public void Sha256Hex_Abc_MatchesKnownVector()
    {
        Assert.Equal(
            "ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad",
            Checksums.Sha256Hex(Encoding.ASCII.GetBytes("abc")));
    }

    [Fact]
    public void Sha256Hex_StreamAndBytes_Agree()
    {
        var data = TestData.Bytes(100_000);
        using var ms = new MemoryStream(data);
        Assert.Equal(Checksums.Sha256Hex(data), Checksums.Sha256Hex(ms));
    }

    [Fact]
    public void Sha256File_And_VerifyFile()
    {
        using var tmp = new TempDir();
        var path = tmp.Combine("blob.bin");
        File.WriteAllBytes(path, Encoding.ASCII.GetBytes("abc"));

        var hex = Checksums.Sha256File(path);
        Assert.Equal("ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad", hex);

        Assert.True(Checksums.VerifyFile(path, hex));
        Assert.True(Checksums.VerifyFile(path, hex.ToUpperInvariant()), "hex comparison must be case-insensitive");
        Assert.True(Checksums.VerifyFile(path, "  " + hex + " \n"), "expected hex must be trimmed");
        Assert.False(Checksums.VerifyFile(path, hex.Replace('b', 'c')));
        Assert.False(Checksums.VerifyFile(path, ""));
        Assert.False(Checksums.VerifyFile(path, "   "));
    }

    [Fact]
    public void Sha1HexUpper_IsUppercase()
    {
        var hex = Checksums.Sha1HexUpper(Encoding.ASCII.GetBytes("ADMIN:ADMIN"));
        Assert.Equal("8301316D0D8448A34FA6D0C6BF1CBFA2B4A1A93A", hex);
    }
}
