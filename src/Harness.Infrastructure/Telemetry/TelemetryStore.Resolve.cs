using Harness.Core.Telemetry;
using Harness.Infrastructure.Findings;
using Harness.Infrastructure.Git;

namespace Harness.Infrastructure.Telemetry;

public sealed partial class TelemetryStore
{
    public static async Task ResolveBestEffortAsync(
        Harness.Infrastructure.Storage.HarnessDatabase database,
        GitRepository repository,
        string worktree,
        string head,
        string by,
        CancellationToken token
    )
    {
        try
        {
            await new TelemetryStore(database)
                .ResolveAsync(repository, worktree, head, by, token)
                .ConfigureAwait(false);
        }
        catch (Exception error)
            when (error
                    is IOException
                        or InvalidDataException
                        or Microsoft.Data.Sqlite.SqliteException
                        or OperationCanceledException
            )
        { /* The triggering action already succeeded; scoring is optional. */
        }
    }

    public async Task<ContextResolution> ResolveAsync(
        GitRepository repository,
        string worktree,
        string head,
        string by,
        CancellationToken token
    )
    {
        if (head.Length is 0)
        {
            return new(0, 0, 0, 0, 0);
        }

        var calls = Query(
            "SELECT id,head,at FROM context_calls WHERE worktree_id=$wt AND resolved_at IS NULL ORDER BY id",
            ("$wt", worktree)
        );
        var cutoff = Stamp(_clock.GetUtcNow().AddDays(-30));
        var count = 0;
        var changedCount = 0;
        var hits = 0;
        var callees = 0;
        var expired = 0;
        void Close(long id, string reason) =>
            database.WithTransaction(
                (c, t) =>
                {
                    Execute(
                        c,
                        t,
                        "UPDATE context_calls SET resolved_at=$at,resolved_by=$by WHERE id=$id AND resolved_at IS NULL",
                        ("$at", Now()),
                        ("$by", reason),
                        ("$id", id)
                    );
                    return 0;
                },
                token
            );
        // calls served at the same head share one list of what changed since (TASK-0100); null: that head is gone
        Dictionary<string, string[]?> changedSince = new(StringComparer.Ordinal);
        List<Pending> pending = [];
        foreach (var call in calls)
        {
            token.ThrowIfCancellationRequested();
            var id = Long(call[0]);
            var old = Text(call[1]);
            if (old == head)
            {
                if (string.CompareOrdinal(Text(call[2]), cutoff) < 0)
                {
                    Close(id, "expired");
                    expired++;
                }
                continue;
            }

            if (!changedSince.TryGetValue(old, out var changed))
            {
                try
                {
                    changed = (await repository.ChangedBetweenAsync(old, head, token).ConfigureAwait(false))
                        .Where(p => p is not ".harness" && !p.StartsWith(".harness/", StringComparison.Ordinal))
                        .ToArray();
                }
                catch (Exception error) when (error is IOException or InvalidDataException)
                {
                    changed = null;
                }

                changedSince[old] = changed;
            }

            if (changed is null)
            {
                Close(id, "lost");
                continue;
            }

            if (changed.Length is 0)
            {
                continue;
            }

            var paths = Query("SELECT DISTINCT path FROM context_units WHERE call_id=$id", ("$id", id))
                .Select(r => Text(r[0]))
                .ToHashSet(StringComparer.Ordinal);
            pending.Add(new(id, old, changed, paths));
        }

        var functions = await FunctionsAsync(repository, head, pending, token).ConfigureAwait(false);
        await using var blobs = repository.OpenBlobReader();
        Dictionary<(string Commit, string Path), string> sources = [];
        foreach (var (id, old, changed, paths) in pending)
        {
            token.ThrowIfCancellationRequested();
            var callers = await CalleeMissesAsync(
                    repository,
                    blobs,
                    old,
                    changed,
                    paths,
                    functions[old],
                    sources,
                    token
                )
                .ConfigureAwait(false);
            var wrote = database.WithTransaction(
                (c, t) =>
                {
                    using var check = FindingStore.Command(
                        c,
                        t,
                        "SELECT resolved_at FROM context_calls WHERE id=$id",
                        ("$id", id)
                    );
                    if (check.ExecuteScalar() is not DBNull)
                    {
                        return false;
                    }

                    foreach (var path in changed)
                    {
                        Execute(
                            c,
                            t,
                            "INSERT INTO context_outcomes(call_id,path,language,hit,called_from) VALUES($id,$path,$language,$hit,$from)",
                            ("$id", id),
                            ("$path", path),
                            ("$language", Language(path)),
                            ("$hit", paths.Contains(path)),
                            ("$from", callers.GetValueOrDefault(path) is { } source ? source : DBNull.Value)
                        );
                    }

                    Execute(
                        c,
                        t,
                        "UPDATE context_calls SET resolved_at=$at,resolved_by=$by,outcome_to=$head WHERE id=$id",
                        ("$at", Now()),
                        ("$by", by),
                        ("$head", head),
                        ("$id", id)
                    );
                    return true;
                },
                token
            );
            if (wrote)
            {
                count++;
                changedCount += changed.Length;
                hits += changed.Count(paths.Contains);
                callees += callers.Count;
            }
        }
        return new(count, changedCount, hits, callees, expired);
    }

    /// <summary>A call whose head moved: what changed since, and the files its answer held.</summary>
    private sealed record Pending(long Id, string Old, string[] Changed, HashSet<string> Paths);

    private static readonly IReadOnlyDictionary<string, IReadOnlyList<string>> NoFunctions =
        new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);

    /// <summary>The changed files a call's answer missed and whose callers are looked for: the first ten.</summary>
    private static IEnumerable<string> Missed(IEnumerable<string> changed, HashSet<string> paths) =>
        changed.Where(p => !paths.Contains(p)).Take(10);

    /// <summary>
    /// The functions changed in every pending call's missed files, by the call's head: one git diff per head over
    /// all of them. When that diff fails, each file is asked alone, so one file git refuses costs only itself.
    /// </summary>
    private static async Task<Dictionary<string, IReadOnlyDictionary<string, IReadOnlyList<string>>>> FunctionsAsync(
        GitRepository repo,
        string to,
        List<Pending> pending,
        CancellationToken token
    )
    {
        Dictionary<string, IReadOnlyDictionary<string, IReadOnlyList<string>>> result = new(StringComparer.Ordinal);
        foreach (var calls in pending.GroupBy(p => p.Old, StringComparer.Ordinal))
        {
            var missed = calls.SelectMany(p => Missed(p.Changed, p.Paths)).Distinct(StringComparer.Ordinal).ToArray();
            if (missed.Length is 0)
            {
                result[calls.Key] = NoFunctions;
                continue;
            }

            try
            {
                result[calls.Key] = await repo.ChangedFunctionsAsync(calls.Key, to, missed, token)
                    .ConfigureAwait(false);
            }
            catch (IOException)
            {
                Dictionary<string, IReadOnlyList<string>> alone = new(StringComparer.Ordinal);
                foreach (var path in missed)
                {
                    try
                    {
                        var one = await repo.ChangedFunctionsAsync(calls.Key, to, [path], token)
                            .ConfigureAwait(false);
                        alone[path] = one[path];
                    }
                    catch (IOException)
                    { /* No names for a file git refuses; the others are still looked for. */
                    }
                }

                result[calls.Key] = alone;
            }
        }

        return result;
    }

    /// <summary>A file as it was at a commit: through the blob reader, or git show when the reader cannot.</summary>
    private static async Task<string> SourceAsync(
        GitRepository repo,
        GitBlobReader blobs,
        string commit,
        string path,
        CancellationToken token
    )
    {
        try
        {
            return await blobs.ReadAsync(commit, path, token).ConfigureAwait(false) ?? "";
        }
        catch (IOException)
        {
            try
            {
                return await repo.FileAtAsync(commit, path, token).ConfigureAwait(false);
            }
            catch (IOException)
            {
                return "";
            }
        }
    }

    private static async Task<Dictionary<string, string>> CalleeMissesAsync(
        GitRepository repo,
        GitBlobReader blobs,
        string from,
        IReadOnlyList<string> changed,
        HashSet<string> paths,
        IReadOnlyDictionary<string, IReadOnlyList<string>> functions,
        Dictionary<(string Commit, string Path), string> sources,
        CancellationToken token
    )
    {
        Dictionary<string, string> result = new(StringComparer.Ordinal);
        foreach (var missed in Missed(changed, paths))
        {
            if (functions.GetValueOrDefault(missed) is not { Count: > 0 } names)
            {
                continue;
            }

            foreach (var returned in paths.Order(StringComparer.Ordinal))
            {
                if (!sources.TryGetValue((from, returned), out var text))
                {
                    text = await SourceAsync(repo, blobs, from, returned, token).ConfigureAwait(false);
                    sources.Add((from, returned), text);
                }
                if (CallsAny(text, names))
                {
                    result[missed] = returned;
                    break;
                }
            }
        }
        return result;
    }

    public static bool CallsAny(string source, IReadOnlyList<string> names)
    {
        foreach (var name in names.Where(n => n.Length > 0))
        {
            for (var index = source.IndexOf(name, StringComparison.Ordinal); index >= 0; )
            {
                var end = index + name.Length;
                var before = index is 0 || !(char.IsAsciiLetterOrDigit(source[index - 1]) || source[index - 1] is '_');
                if (before && source.AsSpan(end).TrimStart(" \t").StartsWith("(", StringComparison.Ordinal))
                {
                    return true;
                }

                index = source.IndexOf(name, end, StringComparison.Ordinal);
            }
        }

        return false;
    }
}
