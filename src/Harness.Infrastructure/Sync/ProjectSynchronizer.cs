using Harness.Core.Projects;
using Harness.Core.Sync;
using Harness.Infrastructure.Projects;
using Harness.Infrastructure.Storage;

namespace Harness.Infrastructure.Sync;

public sealed record ProjectSyncResult(ProjectLocation Location, ProjectRegistration Registration, WorktreeIdentity? Previous,
    IReadOnlyList<WorktreeIdentity> Removed, long CoveredBy, SyncRun? Files, SyncState State)
{
    public IReadOnlyList<Harness.Infrastructure.Findings.FindingSync> Findings { get; init; } = [];
}

public sealed class ProjectSynchronizer(ProjectLifecycle lifecycle, TimeProvider? clock = null, Harness.Core.Providers.IProviderRegistry? providers = null)
{
    public async Task<ProjectSyncResult> RunAsync(string directory, SyncOptions? options = null, CancellationToken token = default)
    {
        options ??= new();
        var location = await lifecycle.LocateAsync(directory, cancellationToken: token).ConfigureAwait(false);
        if (!location.Initialized)
            throw new InvalidDataException("project not initialized; run 'harness init'");
        var repository = location.Repository!;
        var time = clock ?? TimeProvider.System;
        var started = time.GetUtcNow();
        await using var lease = await WorktreeSyncLock.AcquireAsync(repository.GitDir, token).ConfigureAwait(false);
        using var database = HarnessDatabase.Open(location.DatabasePath, new() { BusyTimeout = TimeSpan.FromMilliseconds(location.Configuration.Value.Storage.BusyTimeoutMs) }, token);
        var previous = ProjectStore.Worktrees(database).FirstOrDefault(tree => tree.GitDir == repository.GitDir);
        var head = await repository.ReadHeadAsync(token).ConfigureAwait(false);
        var registration = ProjectStore.Register(database, repository, head, token);
        var worktree = registration.Worktree.Id;
        var covered = options.Mode is "" or "incremental" ? SyncStore.SyncedSince(database, worktree, started, head.Commit) : 0;
        SyncRun? run = null;
        if (covered is 0)
            run = await GitSynchronizer.RunAsync(database, repository, worktree, options with { Ignore = location.Configuration.Value.Sync.Ignore.Concat(options.Ignore).ToArray() }, time, token).ConfigureAwait(false);
        var state = run?.State ?? SyncStore.LoadState(database, worktree) ?? throw new InvalidDataException("load sync state: no recorded state");
        var removed = RemoveGoneWorktrees(database, worktree, token);
        try
        { await lifecycle.Registry.NoteSyncAsync(registration.Project, ProjectStore.Worktrees(database), lifecycle.Version, token).ConfigureAwait(false); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.Text.Json.JsonException or InvalidOperationException) { /* Machine observations are best effort, as for MCP startup. */ }
        var findings = await new Harness.Infrastructure.Findings.FindingSets(location.Root, registration.Project.Id, database, lifecycle.GlobalConfigurationPath, lifecycle.Registry).SyncAsync(head.Commit, providers, token).ConfigureAwait(false);
        return new(location, registration, previous, removed, covered, run, state) { Findings = findings };
    }

    private static WorktreeIdentity[] RemoveGoneWorktrees(HarnessDatabase database, string current, CancellationToken token)
    {
        var gone = ProjectStore.Worktrees(database).Where(tree => tree.Id != current && Missing(tree.GitDir)).ToArray();
        if (gone.Length is 0)
            return gone;
        database.WithTransaction((connection, transaction) =>
        {
            foreach (var tree in gone)
            {
                using var command = SyncStore.Command(connection, transaction, "DELETE FROM files WHERE worktree_id=$wt; DELETE FROM sync_state WHERE worktree_id=$wt; DELETE FROM sync_runs WHERE worktree_id=$wt; DELETE FROM worktrees WHERE id=$wt;", ("$wt", tree.Id));
                command.ExecuteNonQuery();
            }
            return 0;
        }, token);
        return gone;
    }

    private static bool Missing(string path)
    {
        try
        { _ = File.GetAttributes(path); return false; }
        catch (Exception error) when (error is FileNotFoundException or DirectoryNotFoundException) { return true; }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { return false; }
    }
}
