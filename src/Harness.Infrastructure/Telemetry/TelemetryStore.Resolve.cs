using Harness.Core.Telemetry;
using Harness.Infrastructure.Findings;
using Harness.Infrastructure.Git;

namespace Harness.Infrastructure.Telemetry;

public sealed partial class TelemetryStore
{
    public static async Task ResolveBestEffortAsync(Harness.Infrastructure.Storage.HarnessDatabase database, GitRepository repository, string worktree, string head, string by, CancellationToken token)
    {
        try
        { await new TelemetryStore(database).ResolveAsync(repository, worktree, head, by, token).ConfigureAwait(false); }
        catch (Exception error) when (error is IOException or InvalidDataException or Microsoft.Data.Sqlite.SqliteException or OperationCanceledException) { /* The triggering action already succeeded; scoring is optional. */ }
    }
    public async Task<ContextResolution> ResolveAsync(GitRepository repository, string worktree, string head, string by, CancellationToken token)
    {
        if (head.Length is 0)
            return new(0, 0, 0, 0, 0);
        var calls = Query("SELECT id,head,at FROM context_calls WHERE worktree_id=$wt AND resolved_at IS NULL ORDER BY id", ("$wt", worktree));
        var cutoff = Stamp(_clock.GetUtcNow().AddDays(-30));
        var count = 0;
        var changedCount = 0;
        var hits = 0;
        var callees = 0;
        var expired = 0;
        void Close(long id, string reason) => database.WithTransaction((c, t) => { Execute(c, t, "UPDATE context_calls SET resolved_at=$at,resolved_by=$by WHERE id=$id AND resolved_at IS NULL", ("$at", Now()), ("$by", reason), ("$id", id)); return 0; }, token);
        foreach (var call in calls)
        {
            token.ThrowIfCancellationRequested();
            var id = Long(call[0]);
            var old = Text(call[1]);
            if (old == head)
            {
                if (string.CompareOrdinal(Text(call[2]), cutoff) < 0)
                { Close(id, "expired"); expired++; }
                continue;
            }
            string[] changed;
            try
            { changed = (await repository.ChangedBetweenAsync(old, head, token).ConfigureAwait(false)).Where(p => p is not ".harness" && !p.StartsWith(".harness/", StringComparison.Ordinal)).ToArray(); }
            catch (Exception error) when (error is IOException or InvalidDataException) { Close(id, "lost"); continue; }
            if (changed.Length is 0)
                continue;
            var paths = Query("SELECT DISTINCT path FROM context_units WHERE call_id=$id", ("$id", id)).Select(r => Text(r[0])).ToHashSet(StringComparer.Ordinal);
            var callers = await CalleeMissesAsync(repository, old, head, changed, paths, token).ConfigureAwait(false);
            var wrote = database.WithTransaction((c, t) =>
            {
                using var check = FindingStore.Command(c, t, "SELECT resolved_at FROM context_calls WHERE id=$id", ("$id", id));
                if (check.ExecuteScalar() is not DBNull)
                    return false;
                foreach (var path in changed)
                    Execute(c, t, "INSERT INTO context_outcomes(call_id,path,language,hit,called_from) VALUES($id,$path,$language,$hit,$from)", ("$id", id), ("$path", path), ("$language", Language(path)), ("$hit", paths.Contains(path)), ("$from", callers.GetValueOrDefault(path) is { } source ? source : DBNull.Value));
                Execute(c, t, "UPDATE context_calls SET resolved_at=$at,resolved_by=$by,outcome_to=$head WHERE id=$id", ("$at", Now()), ("$by", by), ("$head", head), ("$id", id));
                return true;
            }, token);
            if (wrote)
            { count++; changedCount += changed.Length; hits += changed.Count(paths.Contains); callees += callers.Count; }
        }
        return new(count, changedCount, hits, callees, expired);
    }
    private static async Task<Dictionary<string, string>> CalleeMissesAsync(GitRepository repo, string from, string to, IReadOnlyList<string> changed, HashSet<string> paths, CancellationToken token)
    {
        Dictionary<string, string> result = new(StringComparer.Ordinal);
        Dictionary<string, string> sources = new(StringComparer.Ordinal);
        foreach (var missed in changed.Where(p => !paths.Contains(p)).Take(10))
        {
            IReadOnlyList<string> names;
            try
            { names = await repo.ChangedFunctionsAsync(from, to, missed, token).ConfigureAwait(false); }
            catch (IOException) { continue; }
            if (names.Count is 0)
                continue;
            foreach (var returned in paths.Order(StringComparer.Ordinal))
            {
                if (!sources.TryGetValue(returned, out var text))
                {
                    try
                    { text = await repo.FileAtAsync(from, returned, token).ConfigureAwait(false); }
                    catch (IOException) { text = ""; }
                    sources.Add(returned, text);
                }
                if (CallsAny(text, names))
                { result[missed] = returned; break; }
            }
        }
        return result;
    }
    public static bool CallsAny(string source, IReadOnlyList<string> names)
    {
        foreach (var name in names.Where(n => n.Length > 0))
            for (var index = source.IndexOf(name, StringComparison.Ordinal); index >= 0;)
            {
                var end = index + name.Length;
                var before = index is 0 || !(char.IsAsciiLetterOrDigit(source[index - 1]) || source[index - 1] is '_');
                if (before && source.AsSpan(end).TrimStart(" \t").StartsWith("(", StringComparison.Ordinal))
                    return true;
                index = source.IndexOf(name, end, StringComparison.Ordinal);
            }
        return false;
    }
}
