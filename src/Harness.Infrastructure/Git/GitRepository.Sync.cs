using Harness.Core.Sync;

namespace Harness.Infrastructure.Git;

public sealed partial record GitRepository
{
    public async Task<bool> HasCommitAsync(string commit, CancellationToken token = default)
    {
        ValidateRevision(commit);
        return (await RunAsync(Root, ["cat-file", "-e", commit + "^{commit}"], token).ConfigureAwait(false)).Code is 0;
    }

    public async Task<IReadOnlyList<string>> ListFilesAsync(CancellationToken token = default) =>
        SplitNul(await ReadGitAsync(["ls-files", "-z", "--cached", "--others", "--exclude-standard"], token).ConfigureAwait(false)).Distinct(StringComparer.Ordinal).ToArray();

    public async Task<IReadOnlyList<string>> DirtyPathsAsync(CancellationToken token = default)
    {
        var fields = SplitNul(await ReadGitAsync(["status", "--porcelain=v1", "-z", "--untracked-files=all"], token).ConfigureAwait(false));
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

    public async Task<IReadOnlyList<GitChange>> ChangesSinceAsync(string commit, CancellationToken token = default)
    {
        if (!await HasCommitAsync(commit, token).ConfigureAwait(false))
        {
            throw new InvalidDataException(commit + ": commit not found in repository");
        }

        var fields = SplitNul(await ReadGitAsync(["diff", "--name-status", "-z", "-M", "--no-ext-diff", commit, "--"], token).ConfigureAwait(false));
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

            changes.Add(status switch
            {
                'R' => new("renamed", fields[i + 1], fields[i]),
                'C' => new("added", fields[i + 1]),
                'A' => new("added", fields[i]),
                'D' => new("deleted", fields[i]),
                _ => new("modified", fields[i]),
            });
            i += count;
        }
        var deleted = changes.Select((change, index) => (change, index)).Where(pair => pair.change.Kind is "deleted").ToDictionary(pair => pair.change.Path, pair => pair.index, StringComparer.Ordinal);
        List<string> added = [];
        foreach (var path in SplitNul(await ReadGitAsync(["ls-files", "-z", "--others", "--exclude-standard"], token).ConfigureAwait(false)))
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

    private async Task PairRenamesAsync(string commit, List<GitChange> changes, Dictionary<string, int> deleted, List<string> added, CancellationToken token)
    {
        Dictionary<string, string> blobs = new(StringComparer.Ordinal);
        foreach (var entry in SplitNul(await ReadGitAsync(["ls-tree", "-r", "-z", commit], token).ConfigureAwait(false)))
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
            var hashes = (await ReadGitAsync(["hash-object", "--", .. batch], token).ConfigureAwait(false)).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
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
