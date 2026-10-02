using WindrunnerLauncher.Core.Models;
using WindrunnerLauncher.Core.Persistence;

namespace WindrunnerLauncher.Core.Server;

/// <summary>
/// First-install / repair pipeline: fetch payloads, init MariaDB, import SQL, patch confs.
/// </summary>
public sealed class SetupOrchestrator
{
    public static readonly IReadOnlyList<SetupStageInfo> Catalog =
    [
        new("fetch-server", "Fetch server"),
        new("fetch-sql", "Fetch SQL"),
        new("fetch-mariadb", "Fetch MariaDB"),
        new("fetch-maps", "Fetch maps"),
        new("generate-myini", "Generate my.ini"),
        new("init-datadir", "Initialize database directory"),
        new("start-mysql", "Start MariaDB"),
        new("import-sql", "Import SQL"),
        new("patch-confs", "Patch configuration"),
        new("sync-realmlist", "Sync realmlist"),
        new("ensure-db-user", "Ensure database user"),
        new("stop-mysql", "Stop MariaDB")
    ];

    private readonly LauncherPaths _paths;
    private readonly PortableEnv _env;
    private readonly StateStore _state;
    private readonly MariaDbManager _maria;
    private readonly FetchService _fetch;
    private readonly ConfPatcher _conf;
    private readonly SqlImporter _sql;

    public SetupOrchestrator(
        LauncherPaths paths,
        PortableEnv env,
        StateStore state,
        MariaDbManager maria,
        FetchService fetch,
        ConfPatcher conf,
        SqlImporter sql)
    {
        _paths = paths ?? throw new ArgumentNullException(nameof(paths));
        _env = env ?? throw new ArgumentNullException(nameof(env));
        _state = state ?? throw new ArgumentNullException(nameof(state));
        _maria = maria ?? throw new ArgumentNullException(nameof(maria));
        _fetch = fetch ?? throw new ArgumentNullException(nameof(fetch));
        _conf = conf ?? throw new ArgumentNullException(nameof(conf));
        _sql = sql ?? throw new ArgumentNullException(nameof(sql));
    }

    /// <summary>Runs every first-install stage, reporting progress as each step starts and completes.</summary>
    public async Task RunAsync(IProgress<SetupStage>? progress = null, CancellationToken ct = default, Action<string>? log = null)
    {
        _env.WriteFriendlySettings(_state.Settings.Server);
        Directory.CreateDirectory(_paths.ServerRoot);
        Directory.CreateDirectory(_paths.ServerData);
        Directory.CreateDirectory(_paths.Logs);
        Directory.CreateDirectory(_paths.Conf);

        await StageAsync(progress, "fetch-server", ct, async () =>
        {
            await _fetch.FetchServerAsync(false, ct, log).ConfigureAwait(false);
        }).ConfigureAwait(false);

        await StageAsync(progress, "fetch-sql", ct, async () =>
        {
            await _fetch.FetchSqlAsync(false, ct, log).ConfigureAwait(false);
        }).ConfigureAwait(false);

        await StageAsync(progress, "fetch-mariadb", ct, async () =>
        {
            await _maria.FetchAsync(ct, log).ConfigureAwait(false);
        }).ConfigureAwait(false);

        await StageAsync(progress, "fetch-maps", ct, async () =>
        {
            await _fetch.FetchMapsAsync(false, ct, log).ConfigureAwait(false);
            ServerUtil.AssertMapsPresent(_paths.Maps);
        }).ConfigureAwait(false);

        await StageAsync(progress, "generate-myini", ct, () =>
        {
            _maria.GenerateMyIni();
            return Task.CompletedTask;
        }).ConfigureAwait(false);

        await StageAsync(progress, "init-datadir", ct, async () =>
        {
            await _maria.InitDatadirAsync(ct).ConfigureAwait(false);
        }).ConfigureAwait(false);

        var mysqlWasReady = _maria.IsReady;
        await StageAsync(progress, "start-mysql", ct, async () =>
        {
            await _maria.StartAsync(ct).ConfigureAwait(false);
        }).ConfigureAwait(false);

        try
        {
            await StageAsync(progress, "import-sql", ct, async () =>
            {
                await _sql.ImportAsync(forceReimport: false, ct, log).ConfigureAwait(false);
            }).ConfigureAwait(false);

            await StageAsync(progress, "patch-confs", ct, () =>
            {
                _conf.Apply();
                return Task.CompletedTask;
            }).ConfigureAwait(false);

            await StageAsync(progress, "sync-realmlist", ct, async () =>
            {
                await _sql.SyncRealmlistAsync(ct, log).ConfigureAwait(false);
            }).ConfigureAwait(false);

            await StageAsync(progress, "ensure-db-user", ct, async () =>
            {
                await _sql.EnsureDatabaseUserAsync(ct, log).ConfigureAwait(false);
            }).ConfigureAwait(false);
        }
        finally
        {
            if (!mysqlWasReady)
            {
                await StageAsync(progress, "stop-mysql", ct, async () =>
                {
                    await _maria.StopAsync(ct).ConfigureAwait(false);
                }).ConfigureAwait(false);
            }
            else
            {
                progress?.Report(CreateStage("stop-mysql", completed: true, detail: "Left running (already up before setup)."));
            }
        }
    }

    public static SetupStage CreateStage(string id, bool completed, string? detail = null)
    {
        var index = IndexOf(id);
        var info = Catalog[index];
        return new SetupStage
        {
            Id = info.Id,
            DisplayName = info.DisplayName,
            Completed = completed,
            Detail = detail,
            Index = index + 1,
            Total = Catalog.Count
        };
    }

    private static async Task StageAsync(
        IProgress<SetupStage>? progress,
        string id,
        CancellationToken ct,
        Func<Task> work)
    {
        ct.ThrowIfCancellationRequested();
        progress?.Report(CreateStage(id, completed: false));
        try
        {
            await work().ConfigureAwait(false);
            progress?.Report(CreateStage(id, completed: true));
        }
        catch (Exception ex)
        {
            progress?.Report(CreateStage(id, completed: false, detail: ex.Message));
            throw;
        }
    }

    private static int IndexOf(string id)
    {
        for (var i = 0; i < Catalog.Count; i++)
        {
            if (Catalog[i].Id == id)
                return i;
        }

        throw new ArgumentOutOfRangeException(nameof(id), id, "Unknown setup stage.");
    }
}
