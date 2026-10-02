using System.Text.Json;
using WindrunnerLauncher.Core.Persistence;

namespace WindrunnerLauncher.Core.Tests;

public class JsonStoreTests
{
    [Fact]
    public void LoadOrNew_MissingFile_ReturnsFactoryValue()
    {
        using var tmp = new TempDir();
        var loaded = JsonStore.LoadOrNew(tmp.Combine("nope.json"), () => new LauncherSettings { Language = "xx" });
        Assert.Equal("xx", loaded.Language);
    }

    [Fact]
    public void Save_CreatesParentDirectory_AndRemovesTempFile()
    {
        using var tmp = new TempDir();
        var path = tmp.Combine("deep", "er", "settings.json");
        JsonStore.Save(path, new LauncherSettings());
        Assert.True(File.Exists(path));
        Assert.Single(Directory.EnumerateFiles(Path.GetDirectoryName(path)!));
    }

    [Fact]
    public void Save_FileNameWithoutDirectory_Roundtrips()
    {
        var path = $"settings-{Guid.NewGuid():N}.json";
        try
        {
            JsonStore.Save(path, new LauncherSettings { Language = "de" });
            Assert.Equal("de", JsonStore.LoadOrNew(path, () => new LauncherSettings()).Language);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Save_FailedReplacement_RemovesTemporaryFile()
    {
        using var tmp = new TempDir();
        var path = tmp.Dir("settings.json");

        var error = Record.Exception(() => JsonStore.Save(path, new LauncherSettings()));

        Assert.True(error is IOException or UnauthorizedAccessException);
        Assert.True(Directory.Exists(path));
        Assert.Empty(Directory.EnumerateFiles(tmp.Path));
    }

    [Fact]
    public async Task Save_ConcurrentWrites_LeavesValidJsonAndNoTemporaryFiles()
    {
        using var tmp = new TempDir();
        var path = tmp.Combine("settings.json");

        await Task.WhenAll(Enumerable.Range(0, 20).Select(index => Task.Run(() =>
            JsonStore.Save(path, new LauncherSettings { Language = $"language-{index}" }))));

        var settings = JsonStore.LoadOrNew(path, () => new LauncherSettings());
        Assert.StartsWith("language-", settings.Language);
        Assert.Single(Directory.EnumerateFiles(tmp.Path));
    }

    [Fact]
    public void Save_LoadOrNew_LauncherSettings_Roundtrip()
    {
        using var tmp = new TempDir();
        var path = tmp.Combine("config", "settings.json");
        var original = new LauncherSettings
        {
            Language = "de",
            ShowBranding = false,
            LastSelectedRealmId = "abc123",
            CheckUpdatesOnStartup = false,
            LightMode = true,
            OnboardingCompleted = true,
            InstallChoice = InstallChoice.ClientAndLocalServer,
            ClientPath = @"C:\Games\Turtle",
            ClientIsManagedCopy = false,
            CleanWowExeBackupPath = @"C:\Games\Turtle\WoW-OriginalBackup.exe",
            IgnoredServerRelease = "0.2.0",
            InstalledServerVersion = "0.1.9",
            InstalledClientManifestVersion = "2024.1",
            HealthySelfUpdate = false,
            PreviousLauncherExe = "Launcher.old.exe",
            Server = new ServerFriendlySettings
            {
                RealmName = "MyRealm",
                RealmAddress = "192.168.1.5",
                AuthPort = 3725,
                WorldPort = 8091,
                MysqlPort = 3308,
                MinRandomBots = 5,
                MaxRandomBots = 50,
                StartServerWithClient = false
            },
            VanillaTweaks = new VanillaTweaksSettings
            {
                UseRecommendedPreset = false,
                LargeAddressAware = false,
                FieldOfViewRadians = 1.5,
                CameraDistanceMax = 50,
                FarClip = 500,
                AlwaysAutoLoot = false,
                SoundInBackground = true,
                NameplateRangeTbc = false,
                MoreSoundChannels = false
            }
        };

        JsonStore.Save(path, original);
        var loaded = JsonStore.LoadOrNew<LauncherSettings>(path, () => throw new Xunit.Sdk.XunitException("file must exist"));

        Assert.Equal("de", loaded.Language);
        Assert.False(loaded.ShowBranding);
        Assert.Equal("abc123", loaded.LastSelectedRealmId);
        Assert.False(loaded.CheckUpdatesOnStartup);
        Assert.True(loaded.LightMode);
        Assert.True(loaded.OnboardingCompleted);
        Assert.Equal(InstallChoice.ClientAndLocalServer, loaded.InstallChoice);
        Assert.Equal(@"C:\Games\Turtle", loaded.ClientPath);
        Assert.False(loaded.ClientIsManagedCopy);
        Assert.Equal(@"C:\Games\Turtle\WoW-OriginalBackup.exe", loaded.CleanWowExeBackupPath);
        Assert.Equal("0.2.0", loaded.IgnoredServerRelease);
        Assert.Equal("0.1.9", loaded.InstalledServerVersion);
        Assert.Equal("2024.1", loaded.InstalledClientManifestVersion);
        Assert.False(loaded.HealthySelfUpdate);
        Assert.Equal("Launcher.old.exe", loaded.PreviousLauncherExe);

        Assert.Equal("MyRealm", loaded.Server.RealmName);
        Assert.Equal("192.168.1.5", loaded.Server.RealmAddress);
        Assert.Equal(3725, loaded.Server.AuthPort);
        Assert.Equal(8091, loaded.Server.WorldPort);
        Assert.Equal(3308, loaded.Server.MysqlPort);
        Assert.Equal(5, loaded.Server.MinRandomBots);
        Assert.Equal(50, loaded.Server.MaxRandomBots);
        Assert.False(loaded.Server.StartServerWithClient);

        Assert.False(loaded.VanillaTweaks.UseRecommendedPreset);
        Assert.False(loaded.VanillaTweaks.LargeAddressAware);
        Assert.Equal(1.5, loaded.VanillaTweaks.FieldOfViewRadians);
        Assert.Equal(50, loaded.VanillaTweaks.CameraDistanceMax);
        Assert.Equal(500, loaded.VanillaTweaks.FarClip);
        Assert.False(loaded.VanillaTweaks.AlwaysAutoLoot);
        Assert.True(loaded.VanillaTweaks.SoundInBackground);
        Assert.False(loaded.VanillaTweaks.NameplateRangeTbc);
        Assert.False(loaded.VanillaTweaks.MoreSoundChannels);
    }

    [Fact]
    public void Save_LoadOrNew_Realms_Roundtrip()
    {
        using var tmp = new TempDir();
        var path = tmp.Combine("config", "realms.json");
        var state = new RealmListState
        {
            Realms =
            [
                new RealmEntry
                {
                    Id = RealmEntry.LocalServerId,
                    DisplayName = "Local Server",
                    Address = "127.0.0.1",
                    AuthPort = 3724,
                    InGameRealmName = "TurtleWoW"
                },
                new RealmEntry
                {
                    Id = "turtle-official",
                    DisplayName = "Turtle WoW",
                    Address = "logon.turtle-wow.org",
                    AuthPort = 3724,
                    ClientDirectoryOverride = @"D:\Turtle",
                    ClientExecutable = "WoW_tweaked.exe",
                    ClearWdb = true,
                    ManagedModState = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase)
                    {
                        ["vanillafixes"] = true,
                        ["hd-patch"] = false
                    }
                }
            ]
        };

        JsonStore.Save(path, state);
        var loaded = JsonStore.LoadOrNew(path, () => new RealmListState());

        Assert.Equal(2, loaded.Realms.Count);
        Assert.Equal(RealmEntry.LocalServerId, loaded.Realms[0].Id);
        var remote = loaded.Realms[1];
        Assert.Equal("turtle-official", remote.Id);
        Assert.Equal("Turtle WoW", remote.DisplayName);
        Assert.Equal("logon.turtle-wow.org", remote.Address);
        Assert.Equal(@"D:\Turtle", remote.ClientDirectoryOverride);
        Assert.Equal("WoW_tweaked.exe", remote.ClientExecutable);
        Assert.True(remote.ClearWdb);
        Assert.Equal(2, remote.ManagedModState.Count);
        Assert.True(remote.ManagedModState["vanillafixes"]);
        Assert.False(remote.ManagedModState["hd-patch"]);
    }

    [Fact]
    public void Save_UsesCamelCase_IndentedJson_AndStringEnums()
    {
        using var tmp = new TempDir();
        var path = tmp.Combine("settings.json");
        JsonStore.Save(path, new LauncherSettings { InstallChoice = InstallChoice.ClientAndLocalServer, ClientPath = null });
        var json = File.ReadAllText(path);

        Assert.Contains("\"language\"", json);
        Assert.DoesNotContain("\"Language\"", json);
        Assert.Contains("\n", json);
        Assert.Contains("\"installChoice\": \"ClientAndLocalServer\"", json);
        Assert.DoesNotContain("\"clientPath\"", json);

        using var doc = JsonDocument.Parse(json);
        Assert.Equal(JsonValueKind.Object, doc.RootElement.ValueKind);
    }

    [Fact]
    public void LoadOrNew_IsCaseInsensitive_AndTolerantOfUnknownFields()
    {
        using var tmp = new TempDir();
        var path = tmp.File("settings.json", """
            {
              "Language": "fr",
              "SHOWBRANDING": false,
              "someFutureField": { "nested": true },
              "server": { "authPort": 4000 }
            }
            """);
        var loaded = JsonStore.LoadOrNew(path, () => new LauncherSettings());
        Assert.Equal("fr", loaded.Language);
        Assert.False(loaded.ShowBranding);
        Assert.Equal(4000, loaded.Server.AuthPort);
        Assert.Equal(8090, loaded.Server.WorldPort);
    }

    [Fact]
    public void LoadOrNew_JsonNull_ReturnsFactoryValue()
    {
        using var tmp = new TempDir();
        var path = tmp.File("settings.json", "null");
        var loaded = JsonStore.LoadOrNew(path, () => new LauncherSettings { Language = "fallback" });
        Assert.Equal("fallback", loaded.Language);
    }

    [Fact]
    public void Save_OverwritesExistingFile()
    {
        using var tmp = new TempDir();
        var path = tmp.Combine("settings.json");
        JsonStore.Save(path, new LauncherSettings { Language = "one" });
        JsonStore.Save(path, new LauncherSettings { Language = "two" });
        Assert.Equal("two", JsonStore.LoadOrNew(path, () => new LauncherSettings()).Language);
    }

    [Fact]
    public void SignedManifestEnvelope_Roundtrip()
    {
        using var tmp = new TempDir();
        var path = tmp.Combine("manifest.signed.json");
        var envelope = DevSigningKeys.SignEnvelope(TrustDomain.Client, "{\"schema\":1}");
        JsonStore.Save(path, envelope);
        var loaded = JsonStore.LoadOrNew(path, () => new SignedManifestEnvelope());
        Assert.Equal(envelope.KeyId, loaded.KeyId);
        Assert.Equal(envelope.Algorithm, loaded.Algorithm);
        Assert.Equal(envelope.SignatureB64, loaded.SignatureB64);
        Assert.Equal(envelope.PayloadJson, loaded.PayloadJson);
        Assert.True(Security.Ed25519Signer.VerifyEnvelope(loaded, Security.BuiltInTrust.Keys, TrustDomain.Client));
    }

    [Fact]
    public void ClientManifest_Roundtrip_PreservesAssetsAndEnums()
    {
        using var tmp = new TempDir();
        var path = tmp.Combine("client.json");
        var manifest = new ClientManifest
        {
            Version = "1.18.1",
            Bootstrap = new ClientBootstrap { Url = "https://example.invalid/client.zip", Sha256 = "ab", Size = 12, NestedRoot = "Turtle" },
            Assets =
            [
                new ManagedAsset
                {
                    Id = "hd-patch", Kind = ModKind.Mpq, Destination = "Data/patch-H.mpq", Required = false,
                    Optional = true, DefaultEnabled = true, Dependencies = ["base"], Conflicts = ["ld-patch"],
                    DllsTxtAdd = ["foo.dll"], Recommended = true
                }
            ]
        };
        JsonStore.Save(path, manifest);
        var json = File.ReadAllText(path);
        Assert.Contains("\"kind\": \"Mpq\"", json);

        var loaded = JsonStore.LoadOrNew(path, () => new ClientManifest());
        Assert.Equal("1.18.1", loaded.Version);
        Assert.Equal("Turtle", loaded.Bootstrap!.NestedRoot);
        var asset = Assert.Single(loaded.Assets);
        Assert.Equal(ModKind.Mpq, asset.Kind);
        Assert.Equal(new[] { "base" }, asset.Dependencies);
        Assert.Equal(new[] { "ld-patch" }, asset.Conflicts);
        Assert.Equal(new[] { "foo.dll" }, asset.DllsTxtAdd);
        Assert.True(asset.Recommended);
    }
}
