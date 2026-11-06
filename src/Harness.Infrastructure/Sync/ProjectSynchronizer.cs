using Harness.Core.Projects;
using Harness.Core.Sync;
using Harness.Infrastructure.Git;
using Harness.Infrastructure.Projects;
using Harness.Infrastructure.Storage;

namespace Harness.Infrastructure.Sync;

public sealed record ProjectSyncResult(
    ProjectLocation Location,
    ProjectRegistration Registration,
    WorktreeIdentity? Previous,
    IReadOnlyList<WorktreeIdentity> Removed,
    long CoveredBy,
    SyncRun? Files,
    SyncState State
)
{
    public IReadOnlyList<Harness.Infrastructure.Findings.FindingSync> Findings { get; init; } = [];

    /// <summary>What the exact-complexity worker does for HEAD, if enabled: started, measuring, ready, ...</summary>
    public string Metrics { get; init; } = "";

    /// <summary>What merging the team's shared knowledge changed; null when the file did not change.</summary>
    public Harness.Core.Memory.SharedKnowledgeMerge? Knowledge { get; init; }
}

public sealed class ProjectSynchronizer(
    ProjectLifecycle lifecycle,
    TimeProvider? clock = null,
    Harness.Core.Providers.IProviderRegistry? providers = null,
    Architecture.Metrics.IMetricsLauncher? metricsLauncher = null
)
{
    public async Task<ProjectSyncResult> RunAsync(
        string directory,
        SyncOptions? options = null,
        CancellationToken token = default
    )
    {
        options ??= new();
        var location = await lifecycle.LocateAsync(directory, cancellationToken: token).ConfigureAwait(false);
        if (!location.Initialized)
        {
            throw new InvalidDataException("project not initialized; run 'harness init'");
        }

        var repository = location.Repository!;
        var time = clock ?? TimeProvider.System;
        var started = time.GetUtcNow();
        await using var lease = await WorktreeSyncLock.AcquireAsync(repository.GitDir, token).ConfigureAwait(false);
        // the files listed while finding what changed serve the findings and metrics of this run (TASK-0101)
        using var files = GitRepository.ShareFileLists();
        using var database = HarnessDatabase.Open(
            location.DatabasePath,
            new() { BusyTimeout = TimeSpan.FromMilliseconds(location.Configuration.Value.Storage.BusyTimeoutMs) },
            token
        );
        var previous = ProjectStore.Worktrees(database).FirstOrDefault(tree => tree.GitDir == repository.GitDir);
        var head = await repository.ReadHeadAsync(token).ConfigureAwait(false);
        var registration = ProjectStore.Register(database, repository, head, token);
        var worktree = registration.Worktree.Id;
        var covered = options.Mode is "" or "incremental"
            ? SyncStore.SyncedSince(database, worktree, started, head.Commit)
            : 0;
        SyncRun? run = null;
        WorktreeIdentity[] removed = [];
        Harness.Core.Memory.SharedKnowledgeMerge? knowledge = null;
        IReadOnlyList<Harness.Infrastructure.Findings.FindingSync> findings = [];
        // Harness's own work runs while the providers refresh; only its provider questions wait (TASK-0104).
        async Task AlongsideAsync(Task refreshed)
        {
            removed = RemoveGoneWorktrees(database, worktree, token);
            knowledge = SharedKnowledge(database, location.Root, token);
            await NoteSyncAsync(registration, database, token).ConfigureAwait(false);
            await ResolveTelemetryAsync(database, repository, worktree, head.Commit, token).ConfigureAwait(false);
            findings = await new Harness.Infrastructure.Findings.FindingSets(
                location.Root,
                registration.Project.Id,
                database,
                lifecycle.GlobalConfigurationPath,
                lifecycle.Registry
            )
                .SyncAsync(head.Commit, providers, refreshed, token)
                .ConfigureAwait(false);
        }

        if (covered is 0)
        {
            run = await GitSynchronizer
                .RunAsync(
                    database,
                    repository,
                    worktree,
                    options with
                    {
                        Ignore = location.Configuration.Value.Sync.Ignore.Concat(options.Ignore).ToArray(),
                    },
                    time,
                    AlongsideAsync,
                    token
                )
                .ConfigureAwait(false);
        }
        else
        {
            await AlongsideAsync(Task.CompletedTask).ConfigureAwait(false);
        }

        var state =
            run?.State
            ?? SyncStore.LoadState(database, worktree)
            ?? throw new InvalidDataException("load sync state: no recorded state");
        var metrics = "";
        if (metricsLauncher is not null)
        {
            try
            {
                metrics = await Architecture
                    .Metrics.MetricsScheduler.ScheduleAsync(
                        location,
                        registration.Project.Id,
                        head.Commit,
                        lifecycle.GlobalConfigurationPath,
                        metricsLauncher,
                        token
                    )
                    .ConfigureAwait(false);
            }
            catch (Exception error)
                when (error
                        is IOException
                            or UnauthorizedAccessException
                            or InvalidOperationException
                            or System.ComponentModel.Win32Exception
                )
            { /* The exact-value worker is optional; sync keeps its lexical findings. */
                metrics = "not started: " + error.Message.Split('\n')[0];
            }
        }

        return new(location, registration, previous, removed, covered, run, state)
        {
            Findings = findings,
            Metrics = metrics,
            Knowledge = knowledge,
        };
    }

    private static Harness.Core.Memory.SharedKnowledgeMerge? SharedKnowledge(
        HarnessDatabase database,
        string root,
        CancellationToken token
    )
    {
        try
        {
            return Memory.SharedKnowledgeSync.Refresh(database, root, token);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        { /* An unreadable file is read again on the next sync or knowledge read. */
            return null;
        }
    }

    private async Task NoteSyncAsync(ProjectRegistration registration, HarnessDatabase database, CancellationToken token)
    {
        try
        {
            await lifecycle
                .Registry.NoteSyncAsync(
                    registration.Project,
                    ProjectStore.Worktrees(database),
                    lifecycle.Version,
                    token
                )
                .ConfigureAwait(false);
        }
        catch (Exception error)
            when (error
                    is IOException
                        or UnauthorizedAccessException
                        or System.Text.Json.JsonException
                        or InvalidOperationException
            )
        { /* Machine observations are best effort, as for MCP startup. */
        }
    }

    private async Task ResolveTelemetryAsync(
        HarnessDatabase database,
        GitRepository repository,
        string worktree,
        string commit,
        CancellationToken token
    )
    {
        try
        {
            await new Harness.Infrastructure.Telemetry.TelemetryStore(database, clock)
                .ResolveAsync(repository, worktree, commit, "sync", token)
                .ConfigureAwait(false);
        }
        catch (Exception error)
            when (error is IOException or InvalidDataException or Microsoft.Data.Sqlite.SqliteException)
        { /* Optional scoring cannot fail sync. */
        }
    }

    private static WorktreeIdentity[] RemoveGoneWorktrees(
        HarnessDatabase database,
        string current,
        CancellationToken token
    )
    {
        var gone = ProjectStore.Worktrees(database).Where(tree => tree.Id != current && Missing(tree.GitDir)).ToArray();
        if (gone.Length is 0)
        {
            return gone;
        }

        database.WithTransaction(
            (connection, transaction) =>
            {
                foreach (var tree in gone)
                {
                    using var command = SyncStore.Command(
                        connection,
                        transaction,
                        "DELETE FROM files WHERE worktree_id=$wt; DELETE FROM sync_state WHERE worktree_id=$wt; DELETE FROM sync_runs WHERE worktree_id=$wt; DELETE FROM worktrees WHERE id=$wt;",
                        ("$wt", tree.Id)
                    );
                    command.ExecuteNonQuery();
                }
                return 0;
            },
            token
        );
        return gone;
    }

    private static bool Missing(string path)
    {
        try
        {
            _ = File.GetAttributes(path);
            return false;
        }
        catch (Exception error) when (error is FileNotFoundException or DirectoryNotFoundException)
        {
            return true;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }
}
