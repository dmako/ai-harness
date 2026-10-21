using Harness.Core.Providers;

namespace Harness.Core.Sync;

public sealed record GitChange(string Kind, string Path, string OldPath = "");

public sealed record SyncOptions
{
    public string Mode { get; init; } = "incremental";
    public string Since { get; init; } = "";
    public IReadOnlyList<string> Ignore { get; init; } = [];
    public IReadOnlyList<IProviderRefresher> Refreshers { get; init; } = [];
    public TimeSpan RefreshTimeout { get; init; } = TimeSpan.FromMinutes(10);
}

public sealed record SyncState(string WorktreeId, string Branch, string Commit, bool Detached, bool Dirty, int DirtyPaths,
    string SyncedAt, IReadOnlyList<string> Pending, bool ProvidersStale);

public sealed record FileStats(IReadOnlyList<string> Added, IReadOnlyList<string> Changed, IReadOnlyList<string> Removed, int Unchanged = 0, int Hashed = 0)
{
    public bool Processed => Added.Count + Changed.Count + Removed.Count > 0;
    public static FileStats Empty { get; } = new([], [], []);
}

public sealed record ProviderOutcome(string Name, string Status, string Detail = "");

public sealed record SyncRun
{
    public string Mode { get; init; } = "incremental";
    public string Reason { get; init; } = "";
    public string Base { get; init; } = "";
    public string Head { get; init; } = "";
    public IReadOnlyList<GitChange> Changes { get; init; } = [];
    public FileStats Files { get; init; } = FileStats.Empty;
    public IReadOnlyList<ProviderOutcome> Providers { get; init; } = [];
    public SyncState? State { get; init; }
    public SyncState? Previous { get; init; }
    public long RunId { get; init; }
    public bool BranchSwitch { get; init; }
}

public static class SyncFreshness
{
    public static bool CrossBranch(SyncState? previous, string branch, bool detached) => previous is not null && (previous.Branch != branch || previous.Detached != detached);
    public static string Staleness(SyncState? previous, string branch, string commit, bool detached) => previous switch
    {
        null => "this worktree has never been synced; provider indexes may not reflect it (run 'harness sync')",
        { } state when CrossBranch(state, branch, detached) => "branch changed since the last sync (" + DescribeBranch(state.Branch, state.Detached) + " -> " + DescribeBranch(branch, detached) + "); indexed results may describe the other branch (run 'harness sync')",
        { } state when state.Commit != commit => "HEAD moved since the last sync (" + Short(state.Commit) + " -> " + Short(commit) + "); indexed results may be out of date (run 'harness sync')",
        _ => "",
    };
    public static string DescribeBranch(string branch, bool detached) => detached || branch.Length is 0 ? "detached HEAD" : branch;
    private static string Short(string value) => value.Length > 12 ? value[..12] : value;
}
