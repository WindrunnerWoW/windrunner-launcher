using System.Formats.Tar;
using System.IO.Compression;
using System.Text;
using WindrunnerLauncher.Core.Client;
using WindrunnerLauncher.Core.Downloads;
using WindrunnerLauncher.Core.Persistence;
using WindrunnerLauncher.Core.Platform;
using WindrunnerLauncher.Core.Server;

namespace WindrunnerLauncher.Core.Tests;

public class ClientPathsTests
{
    [Fact]
    public void Resolve_ReusesExistingChildRegardlessOfCase()
    {
        using var tmp = new TempDir();
        var client = tmp.Dir("client");
        Directory.CreateDirectory(Path.Combine(client, "data", "enus"));
        File.WriteAllText(Path.Combine(client, "data", "enus", "realmlist.wtf"), "old");

        var resolved = ClientPaths.Resolve(client, "Data", "enUS", "realmlist.wtf");

        Assert.Equal(
            Path.GetFullPath(Path.Combine(client, "data", "enus", "realmlist.wtf")),
            Path.GetFullPath(resolved));
        Assert.Equal("old", File.ReadAllText(resolved));
    }

    [Fact]
    public void Resolve_UsesCanonicalNameWhenMissing()
    {
        using var tmp = new TempDir();
        var client = tmp.Dir("client");
        Assert.Equal(Path.Combine(client, "WTF", "Config.wtf"), ClientPaths.Resolve(client, "WTF", "Config.wtf"));
    }
}

public class WineHostTests
{
    [Fact]
    public void UnixToWinePath_UsesZDrive()
    {
        Assert.Equal(@"Z:\home\user\client\WoW.exe", WineHost.UnixToWinePath("/home/user/client/WoW.exe"));
    }

    [Fact]
    public void UnixToWinePath_KeepsDriveLetter()
    {
        Assert.Equal(@"C:\Games\WoW.exe", WineHost.UnixToWinePath(@"C:\Games\WoW.exe"));
    }

    [Fact]
    public void IsWindowsImage_DetectsMzHeader()
    {
        using var tmp = new TempDir();
        var pe = tmp.File("WoW.exe", "MZ-not-really");
        var script = tmp.File("wow.sh", "#!/bin/sh\n");
        Assert.True(WineHost.IsWindowsImage(pe));
        Assert.False(WineHost.IsWindowsImage(script));
    }

    [Fact]
    public void PrefixEnvironment_DisablesMonoAndGecko_AndAddsDxvk()
    {
        var plain = WineHost.PrefixEnvironment("/tmp/prefix");
        Assert.Equal("mscoree,mshtml=", plain["WINEDLLOVERRIDES"]);
        Assert.Equal("/tmp/prefix", plain["WINEPREFIX"]);

        var dxvk = WineHost.PrefixEnvironment("/tmp/prefix", dxvk: true);
        Assert.Equal("mscoree,mshtml=;d3d9=n,b", dxvk["WINEDLLOVERRIDES"]);
    }

    [Fact]
    public void IsProtonWrapper_MatchesTheScript_NotTheWineBinary()
    {
        Assert.True(WineHost.IsProtonWrapper("/home/user/.steam/root/compatibilitytools.d/GE-Proton/proton"));
        Assert.False(WineHost.IsProtonWrapper("/home/user/.steam/root/compatibilitytools.d/GE-Proton/files/bin/wine"));
    }

    [Fact]
    public void BuildLaunch_WineBinary_UsesAWin32Prefix()
    {
        using var tmp = new TempDir();
        var wine = tmp.File("lutris-ge/bin/wine", "");
        var prefix = tmp.Combine("prefix");
        var launch = WineHost.BuildLaunch(wine, prefix, "/tmp/WoW.exe", ["-opengl"], dxvk: false, umuRun: null, win32: true);

        Assert.Equal(WineRunnerKind.Wine, launch.Kind);
        Assert.Equal(wine, launch.FileName);
        Assert.Equal(["/tmp/WoW.exe", "-opengl"], launch.Arguments);
        Assert.Equal(prefix, launch.Environment["WINEPREFIX"]);
        Assert.Equal("win32", launch.Environment["WINEARCH"]);
        Assert.Equal(Path.Combine(prefix, "system.reg"), launch.PrefixMarker);
    }

    [Fact]
    public void BuildLaunch_WineWithout32BitSupport_UsesTheDefaultPrefix()
    {
        using var tmp = new TempDir();
        var wine = tmp.File("wine-wow64/bin/wine", "");
        var launch = WineHost.BuildLaunch(wine, tmp.Combine("prefix"), "/tmp/WoW.exe", [], dxvk: false, umuRun: null, win32: false);

        Assert.False(launch.Environment.ContainsKey("WINEARCH"));
    }

    [Fact]
    public void BuildLaunch_ExistingPrefix_KeepsItsArch()
    {
        using var tmp = new TempDir();
        var wine = tmp.File("lutris-ge/bin/wine", "");
        var prefix = tmp.Dir("prefix");
        File.WriteAllText(Path.Combine(prefix, "system.reg"), "WINE REGISTRY Version 2\n#arch=win64\n");

        var launch = WineHost.BuildLaunch(wine, prefix, "/tmp/WoW.exe", [], dxvk: false, umuRun: null, win32: true);

        Assert.False(launch.Environment.ContainsKey("WINEARCH"));
    }

    [Fact]
    public void HasWin32Support_FalseForWine64_TrueWithI386Loader()
    {
        using var tmp = new TempDir();
        var wine64 = tmp.File("runner/bin/wine64", "");
        tmp.File("runner/lib/wine/i386-unix/ntdll.so", "");
        var wine = tmp.File("runner/bin/wine", "");

        Assert.False(WineHost.HasWin32Support(wine64));
        Assert.True(WineHost.HasWin32Support(wine));
    }

    [Fact]
    public void BuildLaunch_UmuWithoutProtonPath_AsksForGeProton()
    {
        if (!string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("PROTONPATH")))
            return;
        using var tmp = new TempDir();
        var umu = tmp.File("bin/umu-run", "");

        var launch = WineHost.BuildLaunch(umu, tmp.Combine("prefix"), "/tmp/WoW.exe", [], dxvk: false, umuRun: null);

        Assert.Equal(WineRunnerKind.Umu, launch.Kind);
        Assert.Equal("GE-Proton", launch.Environment["PROTONPATH"]);
    }

    [Fact]
    public void BuildLaunch_ProtonScript_UsesRunAndCompatData()
    {
        var launch = WineHost.BuildLaunch(
            "/opt/proton/proton", "/tmp/prefix", "/tmp/WoW.exe", ["-opengl"], dxvk: false, umuRun: null);

        Assert.Equal(WineRunnerKind.Proton, launch.Kind);
        Assert.Equal("/opt/proton/proton", launch.FileName);
        Assert.Equal(["run", "/tmp/WoW.exe", "-opengl"], launch.Arguments);
        Assert.Equal("/tmp/prefix", launch.Environment["STEAM_COMPAT_DATA_PATH"]);
        Assert.False(launch.Environment.ContainsKey("WINEARCH"));
        Assert.Equal(Path.Combine("/tmp/prefix", "pfx", "system.reg"), launch.PrefixMarker);
    }

    [Fact]
    public void BuildLaunch_ProtonScript_UsesUmuWhenPresent()
    {
        using var tmp = new TempDir();
        var proton = tmp.File("GE-Proton/proton", "#!/bin/sh\n");
        var prefix = tmp.Dir("prefix");
        var umu = tmp.Combine("umu-run");
        var game = tmp.Combine("WoW.exe");

        var launch = WineHost.BuildLaunch(proton, prefix, game, [], dxvk: true, umuRun: umu);

        Assert.Equal(WineRunnerKind.Umu, launch.Kind);
        Assert.Equal(umu, launch.FileName);
        Assert.Equal([game], launch.Arguments);
        Assert.Equal(Path.GetDirectoryName(proton), launch.Environment["PROTONPATH"]);
        Assert.Equal(prefix, launch.Environment["WINEPREFIX"]);
        Assert.Equal("umu-0", launch.Environment["GAMEID"]);
        Assert.Equal("mscoree,mshtml=;d3d9=n,b", launch.Environment["WINEDLLOVERRIDES"]);
        Assert.Equal(Path.Combine(prefix, "system.reg"), launch.PrefixMarker);
    }

    [Fact]
    public void BuildLaunch_WineInsideProton_UsesTheProtonScript()
    {
        using var tmp = new TempDir();
        var wine = tmp.File("GE-Proton/files/bin/wine", "");
        tmp.File("GE-Proton/proton", "#!/bin/sh\n");

        var launch = WineHost.BuildLaunch(wine, "/tmp/prefix", "/tmp/WoW.exe", [], dxvk: false, umuRun: null);

        Assert.Equal(WineRunnerKind.Proton, launch.Kind);
        Assert.EndsWith($"{Path.DirectorySeparatorChar}proton", launch.FileName);
        Assert.Equal("run", launch.Arguments[0]);
    }
}

public class ServerReleaseNameTests
{
    [Fact]
    public void Find_SelectsTheArchiveForThisOperatingSystem()
    {
        var release = new GitHubRelease
        {
            TagName = "1.0.0",
            Assets =
            [
                new GitHubReleaseAsset { Name = "windrunner-wow-windows-server-abc.zip", BrowserDownloadUrl = "https://example.invalid/win.zip" },
                new GitHubReleaseAsset { Name = "windrunner-wow-linux-server-abc.tar.gz", BrowserDownloadUrl = "https://example.invalid/linux.tar.gz" },
                new GitHubReleaseAsset { Name = "windrunner-wow-sql-abc.zip", BrowserDownloadUrl = "https://example.invalid/sql.zip" }
            ]
        };

        var asset = ServerReleaseNames.Find(release);
        Assert.NotNull(asset);
        if (OperatingSystem.IsLinux())
            Assert.Equal("windrunner-wow-linux-server-abc.tar.gz", asset!.Name);
        else
            Assert.Equal("windrunner-wow-windows-server-abc.zip", asset!.Name);
    }
}

public class ArchiveTarTests
{
    [Fact]
    public void ExtractTarGz_WritesRegularFile_AndSymlinkOffWindows()
    {
        using var tmp = new TempDir();
        var archive = tmp.Combine("mariadb.tar.gz");
        WriteSampleTarGz(archive, includeLink: !OperatingSystem.IsWindows());

        var dest = tmp.Dir("out");
        ArchiveUtil.ExtractTarGz(archive, dest);

        Assert.Equal("daemon", File.ReadAllText(Path.Combine(dest, "mariadb", "bin", "mariadbd")));
        if (!OperatingSystem.IsWindows())
        {
            var link = Path.Combine(dest, "mariadb", "bin", "mysql");
            Assert.True(File.Exists(link) || Directory.Exists(link));
            Assert.Equal("mariadbd", File.ResolveLinkTarget(link, returnFinalTarget: false)?.Name
                ?? new FileInfo(link).LinkTarget);
        }
    }

    [Fact]
    public void ExtractTarGz_CopiesHardLinkContent()
    {
        using var tmp = new TempDir();
        var archive = tmp.Combine("hardlink.tar.gz");
        using (var file = File.Create(archive))
        using (var gzip = new GZipStream(file, CompressionLevel.Fastest))
        using (var writer = new TarWriter(gzip))
        {
            writer.WriteEntry(new PaxTarEntry(TarEntryType.RegularFile, "mariadb/bin/mariadbd")
            {
                DataStream = new MemoryStream(Encoding.UTF8.GetBytes("daemon"))
            });
            writer.WriteEntry(new PaxTarEntry(TarEntryType.HardLink, "mariadb/bin/mysql")
            {
                LinkName = "mariadb/bin/mariadbd"
            });
        }

        var dest = tmp.Dir("out");
        ArchiveUtil.ExtractTarGz(archive, dest);

        Assert.Equal("daemon", File.ReadAllText(Path.Combine(dest, "mariadb", "bin", "mariadbd")));
        Assert.Equal("daemon", File.ReadAllText(Path.Combine(dest, "mariadb", "bin", "mysql")));
    }

    [Fact]
    public void ExtractTarGz_RejectsLinkThatLeavesTheDestination()
    {
        using var tmp = new TempDir();
        var archive = tmp.Combine("escape.tar.gz");
        using (var file = File.Create(archive))
        using (var gzip = new GZipStream(file, CompressionLevel.Fastest))
        using (var writer = new TarWriter(gzip))
        {
            writer.WriteEntry(new PaxTarEntry(TarEntryType.SymbolicLink, "escape")
            {
                LinkName = "../outside"
            });
        }

        var ex = Assert.Throws<InvalidDataException>(() => ArchiveUtil.ExtractTarGz(archive, tmp.Dir("out")));
        Assert.Contains("escapes", ex.Message);
    }

    [Fact]
    public void ExtractTarGz_ReplacesSymlinkInsteadOfFollowingIt()
    {
        if (OperatingSystem.IsWindows())
            return;

        using var tmp = new TempDir();
        var outside = tmp.File("secret.txt", "secret");
        var archive = tmp.Combine("replace.tar.gz");
        using (var file = File.Create(archive))
        using (var gzip = new GZipStream(file, CompressionLevel.Fastest))
        using (var writer = new TarWriter(gzip))
        {
            writer.WriteEntry(new PaxTarEntry(TarEntryType.RegularFile, "mariadb/bin/mariadbd")
            {
                DataStream = new MemoryStream(Encoding.UTF8.GetBytes("daemon"))
            });
        }

        var dest = tmp.Dir("out");
        var link = Path.Combine(dest, "mariadb", "bin", "mariadbd");
        Directory.CreateDirectory(Path.GetDirectoryName(link)!);
        File.CreateSymbolicLink(link, outside);

        ArchiveUtil.ExtractTarGz(archive, dest);

        Assert.Equal("daemon", File.ReadAllText(link));
        Assert.Equal("secret", File.ReadAllText(outside));
    }

    [Fact]
    public void ExtractTarGz_RejectsLinkChainThatLeavesTheDestination()
    {
        if (OperatingSystem.IsWindows())
            return;

        using var tmp = new TempDir();
        var archive = tmp.Combine("chain.tar.gz");
        using (var file = File.Create(archive))
        using (var gzip = new GZipStream(file, CompressionLevel.Fastest))
        using (var writer = new TarWriter(gzip))
        {
            writer.WriteEntry(new PaxTarEntry(TarEntryType.SymbolicLink, "a") { LinkName = "." });
            // As text, a/.. is the destination. On disk, a is the destination, so a/.. is its parent.
            writer.WriteEntry(new PaxTarEntry(TarEntryType.SymbolicLink, "b") { LinkName = "a/.." });
            writer.WriteEntry(new PaxTarEntry(TarEntryType.RegularFile, "b/evil")
            {
                DataStream = new MemoryStream(Encoding.UTF8.GetBytes("evil"))
            });
        }

        var dest = tmp.Dir("out");
        Assert.Throws<InvalidDataException>(() => ArchiveUtil.ExtractTarGz(archive, dest));
        Assert.False(File.Exists(tmp.Combine("evil")));
    }

    [Fact]
    public void ExtractTarGz_RefusesToWriteThroughALinkedFolder()
    {
        if (OperatingSystem.IsWindows())
            return;

        using var tmp = new TempDir();
        var archive = tmp.Combine("through.tar.gz");
        using (var file = File.Create(archive))
        using (var gzip = new GZipStream(file, CompressionLevel.Fastest))
        using (var writer = new TarWriter(gzip))
        {
            writer.WriteEntry(new PaxTarEntry(TarEntryType.Directory, "real"));
            writer.WriteEntry(new PaxTarEntry(TarEntryType.SymbolicLink, "link") { LinkName = "real" });
            writer.WriteEntry(new PaxTarEntry(TarEntryType.RegularFile, "link/file")
            {
                DataStream = new MemoryStream(Encoding.UTF8.GetBytes("x"))
            });
        }

        var ex = Assert.Throws<InvalidDataException>(() => ArchiveUtil.ExtractTarGz(archive, tmp.Dir("out")));
        Assert.Contains("symlink", ex.Message);
    }

    private static void WriteSampleTarGz(string path, bool includeLink)
    {
        using var file = File.Create(path);
        using var gzip = new GZipStream(file, CompressionLevel.Fastest);
        using var writer = new TarWriter(gzip);
        var entry = new PaxTarEntry(TarEntryType.RegularFile, "mariadb/bin/mariadbd")
        {
            DataStream = new MemoryStream(Encoding.UTF8.GetBytes("daemon"))
        };
        writer.WriteEntry(entry);
        if (!includeLink)
            return;
        var link = new PaxTarEntry(TarEntryType.SymbolicLink, "mariadb/bin/mysql")
        {
            LinkName = "mariadbd"
        };
        writer.WriteEntry(link);
    }
}

public class GitHubTokenFileTests
{
    [Fact]
    public void SignOut_DeletesTheTokenFile_AndSettingsStayClear()
    {
        if (OperatingSystem.IsWindows())
            return;

        GitHubSession.AccessToken = null;
        GitHubSession.Login = null;
        try
        {
            using var tmp = new TempDir();
            var paths = tmp.Paths();
            paths.EnsureLayout();
            File.WriteAllText(paths.GitHubTokenFile, "gho_test_token\n");
            File.SetUnixFileMode(paths.GitHubTokenFile, UnixFileMode.UserRead | UnixFileMode.UserWrite);

            var state = new StateStore(paths);
            state.Settings.GitHubLogin = "octocat";
            state.SaveSettings();

            var auth = new GitHubAuthService(state);
            Assert.True(auth.IsSignedIn);
            Assert.Equal("gho_test_token", GitHubSession.AccessToken);
            Assert.DoesNotContain("gho_test_token", File.ReadAllText(paths.SettingsFile));

            auth.SignOut();
            Assert.False(File.Exists(paths.GitHubTokenFile));
            Assert.False(GitHubSession.IsSignedIn);
            Assert.DoesNotContain("gho_test_token", File.ReadAllText(paths.SettingsFile));
        }
        finally
        {
            GitHubSession.AccessToken = null;
            GitHubSession.Login = null;
        }
    }
}
