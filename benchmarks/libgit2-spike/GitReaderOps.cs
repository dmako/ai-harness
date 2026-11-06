// TASK-0098: harness's git commands answered with GitReader (fully managed, read-only). GitReader offers HEAD and
// refs, commits, trees, blobs, work-tree status and untracked files; diffs, rename pairing and path-filtered history
// are written here on top of trees, the way harness would have to.
using System.Runtime.CompilerServices;
using System.Text;
using GitReader;
using GitReader.Primitive;

static class Gr
{
    private sealed class Cache
    {
        public readonly Dictionary<Hash, PrimitiveTree> Trees = [];
        public readonly Dictionary<Hash, PrimitiveCommit> Commits = [];
    }

    private static readonly ConditionalWeakTable<PrimitiveRepository, Cache> Caches = new();

    public static PrimitiveRepository Open(string root) =>
        Repository.Factory.OpenPrimitiveAsync(root).GetAwaiter().GetResult();

    private static Cache Of(PrimitiveRepository repo) => Caches.GetOrCreateValue(repo);

    private static string Hex(Hash hash) => hash.ToString();

    public static PrimitiveCommit? Commit(PrimitiveRepository repo, Hash hash)
    {
        var cache = Of(repo);
        if (cache.Commits.TryGetValue(hash, out var known))
        {
            return known;
        }

        var commit = repo.GetCommitAsync(hash).GetAwaiter().GetResult();
        if (commit is { } found)
        {
            cache.Commits[hash] = found;
        }

        return commit;
    }

    private static PrimitiveTree Tree(PrimitiveRepository repo, Hash hash)
    {
        var cache = Of(repo);
        if (!cache.Trees.TryGetValue(hash, out var tree))
        {
            tree = repo.GetTreeAsync(hash).GetAwaiter().GetResult();
            cache.Trees[hash] = tree;
        }

        return tree;
    }

    private static bool IsDirectory(PrimitiveTreeEntry entry) =>
        entry.SpecialModes is PrimitiveSpecialModes.Directory or PrimitiveSpecialModes.Tree;

    public static Hash Head(PrimitiveRepository repo) =>
        repo.GetCurrentHeadReferenceAsync().GetAwaiter().GetResult()!.Value.Target;

    /// <summary>Every file of a tree with its blob hash, by repository-relative path.</summary>
    public static Dictionary<string, Hash> Files(PrimitiveRepository repo, Hash tree)
    {
        var files = new Dictionary<string, Hash>(StringComparer.Ordinal);
        Flatten(repo, tree, "", files);
        return files;
    }

    private static void Flatten(PrimitiveRepository repo, Hash tree, string prefix, Dictionary<string, Hash> into)
    {
        foreach (var entry in Tree(repo, tree).Children)
        {
            if (IsDirectory(entry))
            {
                Flatten(repo, entry.Hash, prefix + entry.Name + "/", into);
            }
            else
            {
                into[prefix + entry.Name] = entry.Hash;
            }
        }
    }

    /// <summary>Changes between two trees ('A', 'D', 'M' with the blob hashes), skipping subtrees with equal hashes.</summary>
    public static void Diff(PrimitiveRepository repo, Hash? from, Hash? to, string prefix, List<(char Kind, string Path, Hash? Old, Hash? New)> into)
    {
        var a = from is { } f ? Tree(repo, f).Children.ToDictionary(e => e.Name, StringComparer.Ordinal) : [];
        var b = to is { } t ? Tree(repo, t).Children.ToDictionary(e => e.Name, StringComparer.Ordinal) : [];
        foreach (var name in a.Keys.Union(b.Keys, StringComparer.Ordinal))
        {
            var hasOld = a.TryGetValue(name, out var old);
            var hasNew = b.TryGetValue(name, out var @new);
            if (hasOld && hasNew && old.Hash.Equals(@new.Hash) && IsDirectory(old) == IsDirectory(@new))
            {
                continue;
            }

            var path = prefix + name;
            var oldDir = hasOld && IsDirectory(old);
            var newDir = hasNew && IsDirectory(@new);
            if (oldDir || newDir)
            {
                Diff(repo, oldDir ? old.Hash : (Hash?)null, newDir ? @new.Hash : (Hash?)null, path + "/", into);
            }

            var oldFile = hasOld && !oldDir;
            var newFile = hasNew && !newDir;
            if (oldFile && newFile)
            {
                into.Add(('M', path, old.Hash, @new.Hash));
            }
            else if (oldFile)
            {
                into.Add(('D', path, old.Hash, null));
            }
            else if (newFile)
            {
                into.Add(('A', path, null, @new.Hash));
            }
        }
    }

    /// <summary>
    /// Pairs deleted and added paths with the same content as renames. git -M also pairs similar (≥50%) content;
    /// GitReader has no diff, so similarity would be harness's own code.
    /// </summary>
    public static IEnumerable<string> WithExactRenames(List<(char Kind, string Path, Hash? Old, Hash? New)> changes)
    {
        var added = changes.Where(c => c.Kind == 'A' && c.New is not null)
            .GroupBy(c => c.New!.Value).ToDictionary(g => g.Key, g => new Queue<string>(g.Select(c => c.Path)));
        var paired = new HashSet<string>(StringComparer.Ordinal);
        foreach (var c in changes.Where(c => c.Kind == 'D' && c.Old is not null))
        {
            if (added.TryGetValue(c.Old!.Value, out var targets) && targets.Count > 0)
            {
                var target = targets.Dequeue();
                paired.Add(c.Path);
                paired.Add(target);
                yield return $"R {c.Path} -> {target}";
            }
        }

        foreach (var c in changes.Where(c => !paired.Contains(c.Path)))
        {
            yield return $"{c.Kind} {c.Path}";
        }
    }

    /// <summary>Commits reachable from HEAD, newest committer date first (git log's default order).</summary>
    public static IEnumerable<PrimitiveCommit> Walk(PrimitiveRepository repo)
    {
        var queue = new PriorityQueue<PrimitiveCommit, long>();
        var seen = new HashSet<Hash>();
        var head = Head(repo);
        seen.Add(head);
        queue.Enqueue(Commit(repo, head)!.Value, 0);
        while (queue.TryDequeue(out var commit, out _))
        {
            yield return commit;
            foreach (var parent in commit.Parents)
            {
                if (seen.Add(parent) && Commit(repo, parent) is { } p)
                {
                    queue.Enqueue(p, -p.Committer.Date.ToUnixTimeSeconds());
                }
            }
        }
    }

    public static Hash? Entry(PrimitiveRepository repo, Hash tree, string path)
    {
        var current = tree;
        var parts = path.Split('/');
        for (var i = 0; i < parts.Length; i++)
        {
            Hash? next = null;
            foreach (var entry in Tree(repo, current).Children)
            {
                if (entry.Name == parts[i])
                {
                    next = entry.Hash;
                    break;
                }
            }

            if (next is not { } hash)
            {
                return null;
            }

            if (i == parts.Length - 1)
            {
                return hash;
            }

            current = hash;
        }

        return null;
    }

    public static PrimitiveWorkingDirectoryStatus Status(PrimitiveRepository repo) =>
        repo.GetWorkingDirectoryStatusAsync().GetAwaiter().GetResult();

    public static IEnumerable<string> Untracked(PrimitiveRepository repo, PrimitiveWorkingDirectoryStatus status) =>
        repo.GetUntrackedFilesAsync(status).GetAwaiter().GetResult().Select(f => f.Path.Replace('\\', '/'));

    public static IEnumerable<string> Dirty(PrimitiveRepository repo)
    {
        var status = Status(repo);
        return status.StagedFiles.Concat(status.UnstagedFiles)
            .Where(f => f.Status is not GitReader.FileStatus.Unmodified and not GitReader.FileStatus.Ignored)
            .Select(f => f.Path.Replace('\\', '/'))
            .Concat(Untracked(repo, status));
    }

    public static IEnumerable<string> FileList(PrimitiveRepository repo)
    {
        var status = Status(repo);
        return status.ProcessedPaths.Select(p => p.Replace('\\', '/')).Concat(Untracked(repo, status));
    }

    /// <summary>
    /// git diff --name-status &lt;base&gt; --: the base tree against the work tree, built from HEAD's tree plus
    /// GitReader's staged and unstaged changes; untracked files listed apart, as harness does.
    /// </summary>
    public static IEnumerable<string> ChangesSince(PrimitiveRepository repo, Hash baseCommit)
    {
        var status = Status(repo);
        var now = Files(repo, Commit(repo, Head(repo))!.Value.TreeRoot);
        foreach (var file in status.StagedFiles.Concat(status.UnstagedFiles))
        {
            var path = file.Path.Replace('\\', '/');
            if (file.Status is GitReader.FileStatus.Deleted)
            {
                now.Remove(path);
            }
            else if (file.Status is not GitReader.FileStatus.Unmodified and not GitReader.FileStatus.Ignored
                     and not GitReader.FileStatus.Untracked && (file.WorkingTreeHash ?? file.IndexHash) is { } hash)
            {
                now[path] = hash;
            }
        }

        var then = Files(repo, Commit(repo, baseCommit)!.Value.TreeRoot);
        var changes = new List<(char, string, Hash?, Hash?)>();
        foreach (var path in then.Keys.Union(now.Keys, StringComparer.Ordinal))
        {
            var hadOld = then.TryGetValue(path, out var old);
            var hasNew = now.TryGetValue(path, out var @new);
            if (hadOld && hasNew && !old.Equals(@new))
            {
                changes.Add(('M', path, old, @new));
            }
            else if (hadOld && !hasNew)
            {
                changes.Add(('D', path, old, null));
            }
            else if (!hadOld && hasNew)
            {
                changes.Add(('A', path, null, @new));
            }
        }

        return WithExactRenames(changes).Concat(Untracked(repo, status).Select(p => "? " + p));
    }

    public static IEnumerable<string> Churn(PrimitiveRepository repo, DateTimeOffset since)
    {
        foreach (var commit in Walk(repo))
        {
            if (commit.Committer.Date < since)
            {
                yield break;
            }

            if (commit.Parents.Count > 1)
            {
                continue;
            }

            var changes = new List<(char, string, Hash?, Hash?)>();
            Diff(repo, commit.Parents.Count == 1 ? Commit(repo, commit.Parents[0])!.Value.TreeRoot : (Hash?)null, commit.TreeRoot, "", changes);
            foreach (var entry in WithExactRenames(changes))
            {
                yield return entry;
            }
        }
    }

    public static IEnumerable<string> RecentCommits(PrimitiveRepository repo, string[] paths, int limit)
    {
        var found = 0;
        foreach (var commit in Walk(repo))
        {
            if (commit.Parents.Count > 1)
            {
                continue;
            }

            var parentTree = commit.Parents.Count == 1 ? Commit(repo, commit.Parents[0])!.Value.TreeRoot : (Hash?)null;
            if (paths.Any(p => !Equals(Entry(repo, commit.TreeRoot, p), parentTree is { } t ? Entry(repo, t, p) : null)))
            {
                yield return Hex(commit.Hash);
                if (++found == limit)
                {
                    yield break;
                }
            }
        }
    }

    public static IEnumerable<string> ChangedBetween(PrimitiveRepository repo, Hash from, Hash to)
    {
        var changes = new List<(char Kind, string Path, Hash? Old, Hash? New)>();
        Diff(repo, Commit(repo, from)!.Value.TreeRoot, Commit(repo, to)!.Value.TreeRoot, "", changes);
        return changes.Select(c => c.Path);
    }

    public static string FileAt(PrimitiveRepository repo, Hash commit, string path)
    {
        var blob = Entry(repo, Commit(repo, commit)!.Value.TreeRoot, path) ?? throw new FileNotFoundException(path);
        using var stream = repo.OpenBlobAsync(blob).GetAwaiter().GetResult();
        using var reader = new StreamReader(stream, new UTF8Encoding(false));
        return reader.ReadToEnd();
    }
}
