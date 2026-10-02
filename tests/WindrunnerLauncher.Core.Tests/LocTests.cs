using System.Text.Json;
using WindrunnerLauncher.Core.Localization;

namespace WindrunnerLauncher.Core.Tests;

public class LocTests
{
    [Fact]
    public void Default_IsEnglish_AndLoadsEmbeddedEnJson()
    {
        var loc = new Loc();
        Assert.Equal("en", loc.Language);
        Assert.Equal("Play", loc["play"]);
    }

    [Fact]
    public void Indexer_KnownKeys()
    {
        var loc = new Loc();
        Assert.Equal("Update", loc["update"]);
        Assert.Equal("Install Server", loc["install.server"]);
        Assert.Equal("Restart Required", loc["restart.required"]);
        Assert.Equal("Windrunner Launcher", loc["app.title"]);
        Assert.Equal("Light mode", loc["settings.light"]);
        Assert.Equal("Local Server", loc["realm.local"]);
    }

    [Fact]
    public void Indexer_IsCaseInsensitive()
    {
        var loc = new Loc();
        Assert.Equal("Play", loc["PLAY"]);
        Assert.Equal("Play", loc["Play"]);
    }

    [Fact]
    public void Indexer_UnknownKey_ReturnsKeyItself()
    {
        var loc = new Loc();
        Assert.Equal("does.not.exist", loc["does.not.exist"]);
    }

    [Fact]
    public void Format_SubstitutesArguments()
    {
        var loc = new Loc();
        Assert.Equal("Retrying in 5s — attempt 2/5", loc.Format("download.retrying", 5, 2, 5));
        Assert.Equal("Download failed after 3 attempts.", loc.Format("download.failed", 3));
        Assert.Equal("Step 3 of 12 — Fetching MariaDB", loc.Format("setup.step", 3, 12, "Fetching MariaDB"));
    }

    [Fact]
    public void Load_UnknownLanguage_FallsBackToEnglish()
    {
        var loc = new Loc();
        loc.Load("xx-nope");
        Assert.Equal("en", loc.Language);
        Assert.Equal("Play", loc["play"]);
    }

    [Fact]
    public void Load_RaisesChanged()
    {
        var loc = new Loc();
        var raised = 0;
        loc.Changed += () => raised++;
        loc.Load("en");
        Assert.Equal(1, raised);
    }

    [Fact]
    public void Discover_AlwaysIncludesEnglish_AndFindsI18nFiles()
    {
        using var tmp = new TempDir();
        var loc = new Loc();
        loc.Discover(tmp.Paths());
        Assert.Equal(new[] { "en" }, loc.Available);

        tmp.File("i18n/de.json", "{ \"play\": \"Spielen\" }");
        tmp.File("i18n/fr.json", "{ \"play\": \"Jouer\" }");
        tmp.File("i18n/EN.json", "{ }");
        tmp.File("i18n/readme.txt", "not a language");
        loc.Discover(tmp.Paths());

        Assert.Contains("en", loc.Available);
        Assert.Contains("de", loc.Available);
        Assert.Contains("fr", loc.Available);
        Assert.Equal(3, loc.Available.Count);
        Assert.Equal("en", loc.Available[0]);
    }

    [Fact]
    public void Load_DiskPack_OverlaysEmbeddedEnglish_AndDataDirWins()
    {
        using var tmp = new TempDir();
        var exe = tmp.Dir("exe");
        tmp.File("exe/i18n/de.json", "{ \"play\": \"Spielen\" }");
        var loc = new Loc();
        loc.Discover(tmp.Paths(), exe);
        loc.Load("de");

        Assert.Equal("de", loc.Language);
        Assert.Equal("Spielen", loc["play"]);
        Assert.Equal("Update", loc["update"]);

        tmp.File("i18n/de.json", "{ \"play\": \"Daten\" }");
        loc.Discover(tmp.Paths(), exe);
        loc.Load("de");
        Assert.Equal("Daten", loc["play"]);
    }

    [Fact]
    public void EmbeddedEnJson_IsValidFlatStringDictionary_WithRequiredKeys()
    {
        var json = EmbeddedResources.ReadText("en.json");
        using var doc = JsonDocument.Parse(json);
        Assert.Equal(JsonValueKind.Object, doc.RootElement.ValueKind);

        var keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var prop in doc.RootElement.EnumerateObject())
        {
            Assert.Equal(JsonValueKind.String, prop.Value.ValueKind);
            Assert.True(keys.Add(prop.Name), $"duplicate key {prop.Name}");
            Assert.False(string.IsNullOrWhiteSpace(prop.Value.GetString()), $"empty value for {prop.Name}");
        }

        foreach (var required in new[]
                 {
                     "play", "update", "install.server", "restart.required", "onboarding.continue",
                     "onboarding.locate", "status.missing", "server.restart", "download.retrying",
                     "download.failed", "download.cancel", "realm.local",
                     "status.launcher.noclient", "status.launcher.offline", "status.launcher.starting",
                     "status.launcher.ready", "status.launcher.update", "status.launcher.restart",
                     "status.launcher.error", "status.launcher.installing", "status.launcher.updating",
                     "setup.step", "setup.complete", "setup.progress"
                 })
        {
            Assert.Contains(required, keys);
        }
    }

    [Fact]
    public void EveryServerLifecycleState_HasALocalizedLabelKey()
    {
        var loc = new Loc();
        var expected = new Dictionary<ServerLifecycleState, string>
        {
            [ServerLifecycleState.NotInstalled] = "server.notinstalled",
            [ServerLifecycleState.Stopped] = "server.stopped",
            [ServerLifecycleState.StartingDatabase] = "server.starting.db",
            [ServerLifecycleState.StartingAuth] = "server.starting.auth",
            [ServerLifecycleState.InitializingWorld] = "server.init.world",
            [ServerLifecycleState.Ready] = "server.ready",
            [ServerLifecycleState.Stopping] = "server.stopping",
            [ServerLifecycleState.RestartRequired] = "server.restart",
            [ServerLifecycleState.Updating] = "server.updating",
            [ServerLifecycleState.RollingBack] = "server.rollback",
            [ServerLifecycleState.Error] = "server.error"
        };

        foreach (var state in Enum.GetValues<ServerLifecycleState>())
        {
            Assert.True(expected.TryGetValue(state, out var key), $"no key mapping for {state}");
            Assert.NotEqual(key, loc[key]);
        }
    }
}
