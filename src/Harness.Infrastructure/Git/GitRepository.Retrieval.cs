using System.Globalization;
using Harness.Core.Memory;
using Harness.Core.Retrieval;

namespace Harness.Infrastructure.Git;

public sealed partial record GitRepository
{
    public Task<IReadOnlyList<RetrievalCommit>> RecentCommitsAsync(IReadOnlyList<string> paths, int limit, CancellationToken cancellationToken = default) => RecentCommitsAtAsync("HEAD", paths, limit, cancellationToken);
    internal async Task<IReadOnlyList<RetrievalCommit>> RecentCommitsAtAsync(string revision, IReadOnlyList<string> paths, int limit, CancellationToken cancellationToken)
    {
        if (paths.Count is 0 || limit <= 0)
        {
            return [];
        }

        ValidateRevision(revision);
        var output = await ReadGitAsync(["log", "-n", limit.ToString(CultureInfo.InvariantCulture), "--no-merges", "--format=%x1e%H%x1f%ct%x1f%s", "--name-only", revision, "--", .. paths], cancellationToken).ConfigureAwait(false);
        var wanted = paths.ToHashSet(StringComparer.Ordinal);
        List<RetrievalCommit> commits = [];
        foreach (var record in output.Split('\x1e'))
        {
            var text = record.TrimStart('\n');
            var split = text.IndexOf('\n', StringComparison.Ordinal);
            var header = split < 0 ? text : text[..split];
            var files = split < 0 ? "" : text[(split + 1)..];
            var fields = header.Split('\x1f');
            if (fields.Length is not 3)
            {
                continue;
            }

            _ = long.TryParse(fields[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var seconds);
            commits.Add(new(fields[0], fields[2], new MemoryTimestamp(DateTimeOffset.FromUnixTimeSeconds(seconds)), files.Split('\n').Select(path => path.Trim()).Where(wanted.Contains).ToArray()));
        }
        return commits;
    }
}
