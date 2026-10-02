using WindrunnerLauncher.Core.Models;

namespace WindrunnerLauncher.Core.Updates;

/// <summary>Progress stages reported by <see cref="UpdateManager.UpdateServerAsync"/> and its rollback.</summary>
public static class ServerUpdateStages
{
    public static readonly IReadOnlyList<SetupStageInfo> Update =
    [
        new("update-download", "Download release"),
        new("update-stop", "Stop server"),
        new("update-backup-files", "Back up server files"),
        new("update-backup-db", "Back up databases"),
        new("update-replace", "Install new files"),
        new("update-configure", "Patch configuration"),
        new("update-start", "Start server")
    ];

    public static readonly IReadOnlyList<SetupStageInfo> Rollback =
    [
        new("rollback-stop", "Stop server"),
        new("rollback-files", "Restore server files"),
        new("rollback-db", "Restore databases"),
        new("rollback-start", "Start server")
    ];

    public static SetupStage Create(IReadOnlyList<SetupStageInfo> catalog, string id, bool completed, string? detail = null)
    {
        var index = -1;
        for (var i = 0; i < catalog.Count; i++)
        {
            if (catalog[i].Id == id)
            {
                index = i;
                break;
            }
        }

        if (index < 0)
            throw new ArgumentOutOfRangeException(nameof(id), id, "Unknown server update stage.");

        return new SetupStage
        {
            Id = id,
            DisplayName = catalog[index].DisplayName,
            Completed = completed,
            Detail = detail,
            Index = index + 1,
            Total = catalog.Count
        };
    }
}
