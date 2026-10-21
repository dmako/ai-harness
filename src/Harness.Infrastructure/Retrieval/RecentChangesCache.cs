using System.Text.Json;
using Harness.Core.Retrieval;
using Harness.Infrastructure.Git;

namespace Harness.Infrastructure.Retrieval;

/// <summary>Session-bounded history cache; immutable commits are keyed by current HEAD and history metadata.</summary>
internal sealed class RecentChangesCache : IDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Dictionary<string, IReadOnlyList<RetrievalCommit>> _entries = new(StringComparer.Ordinal);
    internal async Task<IReadOnlyList<RetrievalCommit>> ReadAsync(GitRepository repository, IReadOnlyList<string> paths, int limit, CancellationToken token)
    {
        if (paths.Count is 0 || limit <= 0)
        { return []; }
        var head = await repository.ReadHeadAsync(token).ConfigureAwait(false);
        if (head.Commit.Length is 0)
        { return await repository.RecentCommitsAsync(paths, limit, token).ConfigureAwait(false); }
        var metadata = new[] { "shallow", "info/grafts", "config", "packed-refs", "refs/replace" }.Select(path => File.GetLastWriteTimeUtc(Path.Combine(repository.CommonDir, path)).Ticks).ToArray();
        var key = JsonSerializer.Serialize(new { repository.Root, head.Commit, paths, limit, metadata });
        await _gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            if (_entries.TryGetValue(key, out var cached))
            { return cached; }
            var commits = await repository.RecentCommitsAtAsync(head.Commit, paths, limit, token).ConfigureAwait(false);
            if (_entries.Count >= 16)
            { _entries.Clear(); }
            _entries[key] = commits;
            return commits;
        }
        finally
        { _gate.Release(); }
    }
    public void Dispose() => _gate.Dispose();
}
