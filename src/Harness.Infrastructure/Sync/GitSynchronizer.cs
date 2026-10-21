using Harness.Core.Providers;
using Harness.Core.Sync;
using Harness.Infrastructure.Git;
using Harness.Infrastructure.Storage;
using Microsoft.Data.Sqlite;

namespace Harness.Infrastructure.Sync;

/// <summary>File transactions and provider refresh. Call while holding the worktree's sync lease.</summary>
public static class GitSynchronizer
{
    public static async Task<SyncRun> RunAsync(HarnessDatabase database, GitRepository repository, string worktree, SyncOptions options, TimeProvider? clock = null, CancellationToken token = default)
    {
        clock ??= TimeProvider.System;
        var head = await repository.ReadHeadAsync(token).ConfigureAwait(false);
        var previous = SyncStore.LoadState(database, worktree);
        var mode = options.Mode.Length is 0 ? "incremental" : options.Mode;
        var run = new SyncRun { Mode = mode, Head = head.Commit, Previous = previous, RunId = SyncStore.StartRun(database, worktree, mode, clock.GetUtcNow()) };
        try
        {
            run = await SelectModeAsync(run, repository, head, options.Since, token).ConfigureAwait(false);
            var ignorer = new SyncIgnorer(options.Ignore);
            if (run.Mode is "full")
                run = run with { Files = SyncFiles.Replace(database, worktree, repository.Root, ignorer.FilterPaths(await repository.ListFilesAsync(token).ConfigureAwait(false)), token) };
            else if (run.Head.Length > 0)
            {
                var changes = ignorer.Filter(await repository.ChangesSinceAsync(run.Base, token).ConfigureAwait(false));
                var candidates = changes.Where(change => change.Kind is not "deleted").Select(change => change.Path).ToList();
                var deleted = changes.Where(change => change.Kind is "deleted" or "renamed").Select(change => change.Kind is "renamed" ? change.OldPath : change.Path).ToArray();
                foreach (var path in ignorer.FilterPaths(previous?.Pending ?? []).Where(path => !candidates.Contains(path, StringComparer.Ordinal) && !deleted.Contains(path, StringComparer.Ordinal)))
                    candidates.Add(path);
                run = run with { Changes = changes, Files = SyncFiles.Apply(database, worktree, repository.Root, candidates, deleted, token) };
            }
            var outcomes = await RefreshAsync(options, run, token).ConfigureAwait(false);
            run = run with { Providers = outcomes };
            var dirty = await repository.DirtyPathsAsync(token).ConfigureAwait(false);
            var state = new SyncState(worktree, head.Branch, head.Commit, head.Detached, dirty.Count > 0, dirty.Count, HarnessDatabase.FormatTime(clock.GetUtcNow()), dirty, outcomes.Any(outcome => outcome.Status is "failed"));
            SyncStore.SaveState(database, state, token);
            run = run with { State = state };
        }
        catch (Exception error) { Finish(database, run, error, clock); throw; }
        Finish(database, run, null, clock);
        return run;
    }

    private static void Finish(HarnessDatabase database, SyncRun run, Exception? error, TimeProvider clock)
    {
        try
        { SyncStore.FinishRun(database, run, error, clock.GetUtcNow()); }
        catch (SqliteException) { /* A database failure must not replace the sync failure being reported. */ }
    }

    private static async Task<SyncRun> SelectModeAsync(SyncRun run, GitRepository repository, GitHead head, string since, CancellationToken token)
    {
        switch (run.Mode)
        {
            case "since":
                if (since.Length is 0)
                    throw new InvalidDataException("--since needs a commit");
                if (!await repository.HasCommitAsync(since, token).ConfigureAwait(false))
                    throw new InvalidDataException("--since " + since + ": commit not found in repository");
                return run with { Base = since };
            case "full":
                return run with { Reason = "requested" };
            case "incremental":
                return await IncrementalModeAsync(run, repository, head, token).ConfigureAwait(false);
            default:
                throw new InvalidDataException("unknown sync mode \"" + run.Mode + "\"");
        }
    }

    private static async Task<SyncRun> IncrementalModeAsync(SyncRun run, GitRepository repository, GitHead head, CancellationToken token)
    {
        var reason = run.Previous switch
        {
            { } state when SyncFreshness.CrossBranch(state, head.Branch, head.Detached) => "branch switch: " + SyncFreshness.DescribeBranch(state.Branch, state.Detached) + " -> " + SyncFreshness.DescribeBranch(head.Branch, head.Detached),
            null => "first sync of this worktree",
            { Commit.Length: 0 } => "previous sync had no commit",
            { } state when !await repository.HasCommitAsync(state.Commit, token).ConfigureAwait(false) => "previous base " + state.Commit[..Math.Min(12, state.Commit.Length)] + " is no longer in the repository (history rewritten?)",
            _ => "",
        };
        return reason.Length > 0 ? run with { Mode = "full", Reason = reason, BranchSwitch = SyncFreshness.CrossBranch(run.Previous, head.Branch, head.Detached) } : run with { Base = run.Previous!.Commit };
    }

    private static async Task<IReadOnlyList<ProviderOutcome>> RefreshAsync(SyncOptions options, SyncRun run, CancellationToken token)
    {
        var needed = run.Files.Processed || run.Mode is "full" || run.Previous?.ProvidersStale is true;
        var changed = run.Files.Added.Concat(run.Files.Changed).Concat(run.Files.Removed).Order(StringComparer.Ordinal).ToArray();
        List<ProviderOutcome> outcomes = [];
        foreach (var provider in options.Refreshers)
        {
            if (!needed)
            { outcomes.Add(new(provider.Name, "not_needed")); continue; }
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
            deadline.CancelAfter(options.RefreshTimeout > TimeSpan.Zero ? options.RefreshTimeout : TimeSpan.FromMinutes(10));
            try
            {
                var task = Task.Run(() => provider.RefreshAsync(new(run.Mode is "full", changed), deadline.Token), deadline.Token);
                var result = await task.WaitAsync(deadline.Token).ConfigureAwait(false);
                outcomes.Add(new(provider.Name, result.Skipped ? "skipped" : "refreshed", result.Detail));
            }
            catch (Exception error) when (!token.IsCancellationRequested)
            { outcomes.Add(new(provider.Name, "failed", Core.Memory.CredentialRedactor.Redact(error is OperationCanceledException ? "provider refresh timed out" : error.Message))); }
        }
        return outcomes;
    }
}
