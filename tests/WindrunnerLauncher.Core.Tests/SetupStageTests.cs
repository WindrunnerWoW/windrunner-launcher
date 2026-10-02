using WindrunnerLauncher.Core.Localization;
using WindrunnerLauncher.Core.Models;
using WindrunnerLauncher.Core.Server;

namespace WindrunnerLauncher.Core.Tests;

public class SetupStageTests
{
    [Fact]
    public void Catalog_HasTwelveUniqueStages()
    {
        Assert.Equal(12, SetupOrchestrator.Catalog.Count);
        Assert.Equal(SetupOrchestrator.Catalog.Count, SetupOrchestrator.Catalog.Select(s => s.Id).Distinct().Count());
        Assert.All(SetupOrchestrator.Catalog, stage =>
        {
            Assert.False(string.IsNullOrWhiteSpace(stage.Id));
            Assert.False(string.IsNullOrWhiteSpace(stage.DisplayName));
        });
    }

    [Fact]
    public void CreateStage_SetsIndexAndTotal()
    {
        var stage = SetupOrchestrator.CreateStage("import-sql", completed: false);

        Assert.Equal("import-sql", stage.Id);
        Assert.Equal("Import SQL", stage.DisplayName);
        Assert.Equal(8, stage.Index);
        Assert.Equal(12, stage.Total);
        Assert.False(stage.Completed);
        Assert.True(stage.IsPending);
        Assert.Equal("○", stage.Glyph);
    }

    [Fact]
    public void CreateStage_UnknownId_Throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => SetupOrchestrator.CreateStage("nope", true));
    }

    [Fact]
    public void SetupStage_ReportsActiveCompletedAndFailedGlyphs()
    {
        var running = new SetupStage { Active = true, Index = 2, Total = 12 };
        Assert.Equal("◆", running.Glyph);
        Assert.False(running.IsPending);
        Assert.False(running.IsDimmed);

        var done = new SetupStage { Completed = true, Index = 1, Total = 12 };
        Assert.Equal("✓", done.Glyph);
        Assert.True(done.IsDimmed);

        var failed = new SetupStage { Detail = "disk full", Index = 4, Total = 12 };
        Assert.True(failed.Failed);
        Assert.True(failed.HasDetail);
        Assert.Equal("✕", failed.Glyph);
        Assert.False(failed.IsDimmed);
    }

    [Fact]
    public void EveryCatalogStage_HasALocalizedName()
    {
        var loc = new Loc();
        foreach (var stage in SetupOrchestrator.Catalog)
        {
            var key = $"setup.stage.{stage.Id}";
            Assert.NotEqual(key, loc[key]);
        }
    }
}
