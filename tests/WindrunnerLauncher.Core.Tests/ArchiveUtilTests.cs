using System.IO.Compression;

namespace WindrunnerLauncher.Core.Tests;

public class ArchiveUtilTests
{
    private static string MakeZip(TempDir tmp, string name, Action<ZipArchive> populate)
    {
        var path = tmp.Combine(name);
        using var fs = File.Create(path);
        using var zip = new ZipArchive(fs, ZipArchiveMode.Create, leaveOpen: false);
        populate(zip);
        return path;
    }

    private static void AddText(ZipArchive zip, string entryName, string text)
    {
        var entry = zip.CreateEntry(entryName);
        using var w = new StreamWriter(entry.Open(), new UTF8Encoding(false));
        w.Write(text);
    }

    [Fact]
    public void ExtractZip_ExtractsFilesAndDirectories()
    {
        using var tmp = new TempDir();
        var zip = MakeZip(tmp, "ok.zip", z =>
        {
            AddText(z, "WoW.exe", "MZ");
            AddText(z, "Data/patch-A.mpq", "mpq");
            z.CreateEntry("Data/enUS/");
            AddText(z, "Data/enUS/realmlist.wtf", "set realmlist \"127.0.0.1\"");
        });

        var dest = tmp.Combine("out");
        ArchiveUtil.ExtractZip(zip, dest);

        Assert.Equal("MZ", File.ReadAllText(Path.Combine(dest, "WoW.exe")));
        Assert.Equal("mpq", File.ReadAllText(Path.Combine(dest, "Data", "patch-A.mpq")));
        Assert.True(Directory.Exists(Path.Combine(dest, "Data", "enUS")));
        Assert.True(File.Exists(Path.Combine(dest, "Data", "enUS", "realmlist.wtf")));
    }

    [Fact]
    public void ExtractZip_OverwritesExistingFiles()
    {
        using var tmp = new TempDir();
        var zip = MakeZip(tmp, "a.zip", z => AddText(z, "file.txt", "new"));
        var dest = tmp.Dir("out");
        File.WriteAllText(Path.Combine(dest, "file.txt"), "old");
        ArchiveUtil.ExtractZip(zip, dest);
        Assert.Equal("new", File.ReadAllText(Path.Combine(dest, "file.txt")));
    }

    [Theory]
    [InlineData("../evil.txt")]
    [InlineData("../../evil.txt")]
    [InlineData("sub/../../evil.txt")]
    [InlineData("sub/./../../evil.txt")]
    public void ExtractZip_ZipSlipEntry_IsRejected(string entryName)
    {
        using var tmp = new TempDir();
        var zip = MakeZip(tmp, "slip.zip", z =>
        {
            AddText(z, "good.txt", "ok");
            AddText(z, entryName, "pwned");
        });

        var dest = tmp.Combine("sandbox", "out");
        var ex = Assert.Throws<InvalidDataException>(() => ArchiveUtil.ExtractZip(zip, dest));
        Assert.Contains("escapes", ex.Message, StringComparison.OrdinalIgnoreCase);

        Assert.False(File.Exists(tmp.Combine("sandbox", "evil.txt")));
        Assert.False(File.Exists(tmp.Combine("evil.txt")));
        Assert.False(File.Exists(Path.Combine(dest, "evil.txt")));
    }

    [Fact]
    public void ExtractZip_ZipSlipDirectoryEntry_IsRejected()
    {
        using var tmp = new TempDir();
        var zip = MakeZip(tmp, "slipdir.zip", z => z.CreateEntry("../outside/"));
        var dest = tmp.Combine("sandbox", "out");
        Assert.Throws<InvalidDataException>(() => ArchiveUtil.ExtractZip(zip, dest));
        Assert.False(Directory.Exists(tmp.Combine("sandbox", "outside")));
    }

    [Fact]
    public void ExtractZip_AbsolutePathEntry_IsRejected()
    {
        using var tmp = new TempDir();
        var outside = tmp.Combine("outside.txt");
        // ZipArchive stores the name verbatim; Path.Combine with a rooted second arg returns the rooted path.
        var zip = MakeZip(tmp, "abs.zip", z => AddText(z, outside, "pwned"));
        var dest = tmp.Combine("sandbox");
        Assert.Throws<InvalidDataException>(() => ArchiveUtil.ExtractZip(zip, dest));
        Assert.False(File.Exists(outside));
    }

    [Fact]
    public void ExtractZip_SiblingPrefixDirectory_IsRejected()
    {
        // "out" vs "out-evil": a naive StartsWith(root) check without a trailing separator would allow this.
        using var tmp = new TempDir();
        var zip = MakeZip(tmp, "prefix.zip", z => AddText(z, "../out-evil/x.txt", "pwned"));
        var dest = tmp.Combine("out");
        Assert.Throws<InvalidDataException>(() => ArchiveUtil.ExtractZip(zip, dest));
        Assert.False(Directory.Exists(tmp.Combine("out-evil")));
    }

    [Fact]
    public void ZipDirectory_ExtractZip_Roundtrip()
    {
        using var tmp = new TempDir();
        var src = tmp.Dir("src");
        File.WriteAllText(Path.Combine(src, "a.txt"), "A");
        Directory.CreateDirectory(Path.Combine(src, "nested", "deeper"));
        File.WriteAllBytes(Path.Combine(src, "nested", "deeper", "b.bin"), TestData.Bytes(5000));

        var zipPath = tmp.Combine("archives", "src.zip");
        ArchiveUtil.ZipDirectory(src, zipPath);
        Assert.True(File.Exists(zipPath));

        var dest = tmp.Combine("dest");
        ArchiveUtil.ExtractZip(zipPath, dest);
        Assert.Equal("A", File.ReadAllText(Path.Combine(dest, "a.txt")));
        Assert.Equal(
            File.ReadAllBytes(Path.Combine(src, "nested", "deeper", "b.bin")),
            File.ReadAllBytes(Path.Combine(dest, "nested", "deeper", "b.bin")));
    }

    [Fact]
    public void ZipDirectory_ReplacesExistingArchive()
    {
        using var tmp = new TempDir();
        var src = tmp.Dir("src");
        File.WriteAllText(Path.Combine(src, "v1.txt"), "1");
        var zipPath = tmp.Combine("x.zip");
        ArchiveUtil.ZipDirectory(src, zipPath);

        File.Delete(Path.Combine(src, "v1.txt"));
        File.WriteAllText(Path.Combine(src, "v2.txt"), "2");
        ArchiveUtil.ZipDirectory(src, zipPath);

        using var zip = ZipFile.OpenRead(zipPath);
        Assert.Single(zip.Entries);
        Assert.Equal("v2.txt", zip.Entries[0].FullName);
    }

    [Fact]
    public void FindNestedRoot_DirectRoot()
    {
        using var tmp = new TempDir();
        tmp.File("x/WoW.exe");
        Assert.Equal(tmp.Combine("x"), ArchiveUtil.FindNestedRoot(tmp.Combine("x"), "WoW.exe"));
    }

    [Fact]
    public void FindNestedRoot_OneLevelDown()
    {
        using var tmp = new TempDir();
        tmp.File("x/Turtle WoW/WoW.exe");
        tmp.File("x/Turtle WoW/Data/base.mpq");
        Assert.Equal(tmp.Combine("x", "Turtle WoW"), ArchiveUtil.FindNestedRoot(tmp.Combine("x"), "WoW.exe", "Data/base.mpq"));
    }

    [Fact]
    public void FindNestedRoot_TwoLevelsDown()
    {
        using var tmp = new TempDir();
        tmp.File("x/release/client/WoW.exe");
        Assert.Equal(tmp.Combine("x", "release", "client"), ArchiveUtil.FindNestedRoot(tmp.Combine("x"), "WoW.exe"));
    }

    [Fact]
    public void FindNestedRoot_ThreeLevelsDown_IsNotSearched()
    {
        using var tmp = new TempDir();
        tmp.File("x/a/b/c/WoW.exe");
        Assert.Null(ArchiveUtil.FindNestedRoot(tmp.Combine("x"), "WoW.exe"));
    }

    [Fact]
    public void FindNestedRoot_RequiresAllFiles()
    {
        using var tmp = new TempDir();
        tmp.File("x/sub/WoW.exe");
        Assert.Null(ArchiveUtil.FindNestedRoot(tmp.Combine("x"), "WoW.exe", "realmd.conf.dist"));
        Assert.NotNull(ArchiveUtil.FindNestedRoot(tmp.Combine("x"), "WoW.exe"));
    }

    [Fact]
    public void FindNestedRoot_Missing_ReturnsNull()
    {
        using var tmp = new TempDir();
        tmp.Dir("empty");
        Assert.Null(ArchiveUtil.FindNestedRoot(tmp.Combine("empty"), "WoW.exe"));
    }
}
