using System.Collections.Concurrent;
using System.Text.RegularExpressions;
using Harness.Core.Sync;

namespace Harness.Infrastructure.Git;

public sealed partial record GitRepository
{
    public async Task<bool> HasCommitAsync(string commit, CancellationToken token = default)
    {
        ValidateRevision(commit);
        return (await RunAsync(Root, ["cat-file", "-e", commit + "^{commit}"], token).ConfigureAwait(false)).Code is 0;
    }

    /// <summary>The file lists already read in this flow, by repository root (see <see cref="ShareFileLists"/>).</summary>
    private static readonly AsyncLocal<ConcurrentDictionary<string, IReadOnlyList<string>>?> SharedFileLists = new();

    /// <summary>
    /// Until disposed, each repository's file list is read once in this flow (and the calls it awaits) and reused
    /// (TASK-0101): a sync lists its files while finding what changed, and its findings and metrics read the same
    /// list instead of starting git again. Only for work that reads a stable tree, as a sync under its lease does.
    /// </summary>
    public static IDisposable ShareFileLists()
    {
        var previous = SharedFileLists.Value;
        SharedFileLists.Value = new(PathComparer);
        return new Restore(() => SharedFileLists.Value = previous);
    }

    private sealed class Restore(Action restore) : IDisposable
    {
        public void Dispose() => restore();
    }

    public async Task<IReadOnlyList<string>> ListFilesAsync(CancellationToken token = default)
    {
        if (SharedFileLists.Value is { } shared && shared.TryGetValue(Root, out var known))
        {
            return known;
        }

        var files = SplitNul(
                await ReadGitAsync(["ls-files", "-z", "--cached", "--others", "--exclude-standard"], token)
                    .ConfigureAwait(false)
            )
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        SharedFileLists.Value?.TryAdd(Root, files);
        return files;
    }

    /// <summary>
    /// The untracked files (what <c>ls-files --others --exclude-standard</c> lists). Inside
    /// <see cref="ShareFileLists"/>, read with the tracked ones in one <c>ls-files -t</c> whose full list is kept
    /// for <see cref="ListFilesAsync"/>.
    /// </summary>
    private async Task<string[]> UntrackedAsync(CancellationToken token)
    {
        if (SharedFileLists.Value is not { } shared)
        {
            return SplitNul(
                await ReadGitAsync(["ls-files", "-z", "--others", "--exclude-standard"], token).ConfigureAwait(false)
            );
        }

        // each entry is "<tag> <path>": '?' marks an untracked file, the other tags tracked ones
        var entries = SplitNul(
            await ReadGitAsync(["ls-files", "-z", "-t", "--cached", "--others", "--exclude-standard"], token)
                .ConfigureAwait(false)
        );
        shared.TryAdd(Root, entries.Select(entry => entry[2..]).Distinct(StringComparer.Ordinal).ToArray());
        return [.. entries.Where(entry => entry[0] is '?').Select(entry => entry[2..])];
    }

    public async Task<IReadOnlyList<string>> DirtyPathsAsync(CancellationToken token = default)
    {
        var fields = SplitNul(
            await ReadGitAsync(["status", "--porcelain=v1", "-z", "--untracked-files=all"], token).ConfigureAwait(false)
        );
        List<string> paths = [];
        var i = 0;
        while (i < fields.Length)
        {
            var field = fields[i++];
            if (field.Length < 4)
            {
                continue;
            }

            paths.Add(field[3..]);
            if (field[0] is 'R' or 'C')
            {
                i++;
            }
        }
        return paths;
    }

    public Task<IReadOnlyList<GitChange>> ChangesSinceAsync(string commit, CancellationToken token = default) =>
        ChangesSinceAsync(commit, verified: false, token);

    /// <summary>
    /// What changed in the work tree since <paramref name="commit"/>; <paramref name="verified"/> when the caller
    /// has just checked that the commit exists (a sync's mode selection does), so it is not checked again.
    /// </summary>
    public async Task<IReadOnlyList<GitChange>> ChangesSinceAsync(
        string commit,
        bool verified,
        CancellationToken token = default
    )
    {
        if (verified)
        {
            ValidateRevision(commit);
        }
        else if (!await HasCommitAsync(commit, token).ConfigureAwait(false))
        {
            throw new InvalidDataException(commit + ": commit not found in repository");
        }

        var fields = SplitNul(
            await ReadGitAsync(["diff", "--name-status", "-z", "-M", "--no-ext-diff", commit, "--"], token)
                .ConfigureAwait(false)
        );
        List<GitChange> changes = [];
        var i = 0;
        while (i < fields.Length)
        {
            var status = fields[i++][0];
            var count = status is 'R' or 'C' ? 2 : 1;
            if (i + count > fields.Length)
            {
                throw new InvalidDataException("Git returned an incomplete name-status record.");
            }

            changes.Add(
                status switch
                {
                    'R' => new("renamed", fields[i + 1], fields[i]),
                    'C' => new("added", fields[i + 1]),
                    'A' => new("added", fields[i]),
                    'D' => new("deleted", fields[i]),
                    _ => new("modified", fields[i]),
                }
            );
            i += count;
        }
        var deleted = changes
            .Select((change, index) => (change, index))
            .Where(pair => pair.change.Kind is "deleted")
            .ToDictionary(pair => pair.change.Path, pair => pair.index, StringComparer.Ordinal);
        List<string> added = [];
        foreach (var path in await UntrackedAsync(token).ConfigureAwait(false))
        {
            if (deleted.Remove(path, out var index))
            {
                changes[index] = new("modified", path);
            }
            else
            {
                added.Add(path);
            }
        }
        if (added.Count > 0 && deleted.Count > 0)
        {
            await PairRenamesAsync(commit, changes, deleted, added, token).ConfigureAwait(false);
        }

        changes.AddRange(added.Select(path => new GitChange("added", path)));
        return changes;
    }

    private async Task PairRenamesAsync(
        string commit,
        List<GitChange> changes,
        Dictionary<string, int> deleted,
        List<string> added,
        CancellationToken token
    )
    {
        Dictionary<string, string> blobs = new(StringComparer.Ordinal);
        foreach (
            var entry in SplitNul(await ReadGitAsync(["ls-tree", "-r", "-z", commit], token).ConfigureAwait(false))
        )
        {
            var split = entry.IndexOf('\t', StringComparison.Ordinal);
            if (split < 0 || !deleted.ContainsKey(entry[(split + 1)..]))
            {
                continue;
            }

            var metadata = entry[..split].Split(' ');
            if (metadata.Length is 3)
            {
                blobs[metadata[2]] = entry[(split + 1)..];
            }
        }
        // Batch conservatively so long path lists fit Windows' command-line limit.
        List<string> remaining = [];
        foreach (var batch in added.Chunk(32))
        {
            var hashes = (await ReadGitAsync(["hash-object", "--", .. batch], token).ConfigureAwait(false)).Split(
                (char[]?)null,
                StringSplitOptions.RemoveEmptyEntries
            );
            for (var i = 0; i < batch.Length; i++)
            {
                if (i < hashes.Length && blobs.Remove(hashes[i], out var old))
                {
                    changes[deleted[old]] = new("renamed", batch[i], old);
                }
                else
                {
                    remaining.Add(batch[i]);
                }
            }
        }
        added.Clear();
        added.AddRange(remaining);
    }

    /// <summary>
    /// The rule that makes Git ignore this path of the worktree, as "source:line:pattern"; empty when Git does not
    /// ignore it.
    /// </summary>
    public async Task<string> IgnoreRuleAsync(string path, CancellationToken token)
    {
        var result = await RunAsync(Root, ["check-ignore", "-v", "--", path], token).ConfigureAwait(false);
        var rule = result.Output.Split('\t')[0].Trim();
        return result.Code switch
        {
            // with -v Git also reports a matching negation ("!pattern"), which re-includes the path
            0 when IgnoreRule.Match(rule) is { Success: true } match && !match.Groups[1].Value.StartsWith('!') => rule,
            0 or 1 => "",
            _ => throw new IOException("git check-ignore: " + result.Error.Trim()),
        };
    }

    [GeneratedRegex(@"\A.+?:\d+:(.*)\z", RegexOptions.CultureInvariant | RegexOptions.Singleline, 1000)]
    private static partial Regex IgnoreRule { get; }

    private async Task<string> ReadGitAsync(string[] arguments, CancellationToken token)
    {
        var result = await RunAsync(Root, arguments, token).ConfigureAwait(false);
        if (result.Code is not 0)
        {
            throw new IOException("git " + arguments[0] + ": " + result.Error.Trim());
        }

        return result.Output;
    }

    private static string[] SplitNul(string output) => output.Split('\0', StringSplitOptions.RemoveEmptyEntries);

    private static void ValidateRevision(string value)
    {
        if (string.IsNullOrEmpty(value) || value[0] is '-' || value.Contains('\0', StringComparison.Ordinal))
        {
            throw new InvalidDataException("Invalid Git revision.");
        }
    }
}
