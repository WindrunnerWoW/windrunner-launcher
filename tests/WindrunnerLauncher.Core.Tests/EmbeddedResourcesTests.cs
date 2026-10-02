namespace WindrunnerLauncher.Core.Tests;

public class EmbeddedResourcesTests
{
    [Theory]
    [InlineData("portable.env")]
    [InlineData("my.ini.template")]
    [InlineData("en.json")]
    public void RequiredResources_AreEmbedded(string name)
    {
        var text = EmbeddedResources.ReadText(name);
        Assert.False(string.IsNullOrWhiteSpace(text));
    }

    [Fact]
    public void Open_Missing_Throws()
    {
        Assert.Throws<FileNotFoundException>(() => EmbeddedResources.Open("definitely-missing.bin"));
    }

    [Fact]
    public void ReadBytes_MatchesReadText()
    {
        var bytes = EmbeddedResources.ReadBytes("portable.env");
        var text = EmbeddedResources.ReadText("portable.env");
        Assert.Equal(text, Encoding.UTF8.GetString(bytes).TrimStart('\uFEFF'));
    }

    [Fact]
    public void PortableEnv_ContainsServerDefaults()
    {
        var text = EmbeddedResources.ReadText("portable.env");
        Assert.Contains("MYSQL_PORT=3307", text);
        Assert.Contains("REALM_PORT=3724", text);
        Assert.Contains("WORLD_PORT=8090", text);
        Assert.Contains("MARIADB_VERSION=", text);
    }

    [Fact]
    public void ExtractTo_WritesFile_AndRespectsOverwriteFlag()
    {
        using var tmp = new TempDir();
        var dest = tmp.Combine("server", "portable.env");
        EmbeddedResources.ExtractTo("portable.env", dest);
        Assert.True(File.Exists(dest));
        var original = File.ReadAllText(dest);

        File.WriteAllText(dest, "USER_EDITED=1");
        EmbeddedResources.ExtractTo("portable.env", dest);
        Assert.Equal("USER_EDITED=1", File.ReadAllText(dest));

        EmbeddedResources.ExtractTo("portable.env", dest, overwrite: true);
        Assert.Equal(original, File.ReadAllText(dest));
    }

    [Fact]
    public void Open_AcceptsPathSeparators()
    {
        using var a = EmbeddedResources.Open("Localization/en.json");
        using var b = EmbeddedResources.Open(@"Localization\en.json");
        Assert.True(a.Length > 0);
        Assert.Equal(a.Length, b.Length);
    }
}
