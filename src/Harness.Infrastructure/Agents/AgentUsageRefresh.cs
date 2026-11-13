using System.Globalization;
using System.Text.Json.Nodes;
using Harness.Core.Tasks;
using Harness.Infrastructure.Storage;
using Microsoft.Data.Sqlite;

namespace Harness.Infrastructure.Agents;

/// <summary>Where the clients keep their transcripts: Claude Code's projects folder and Codex's home.</summary>
public sealed record TranscriptRoots(string ClaudeProjects, string CodexHome)
{
    public static TranscriptRoots Default()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var claude = Environment.GetEnvironmentVariable("CLAUDE_CONFIG_DIR") is { Length: > 0 } configured
            ? configured
            : Path.Combine(home, ".claude");
        var codex = Environment.GetEnvironmentVariable("CODEX_HOME") is { Length: > 0 } codexHome
            ? codexHome
            : Path.Combine(home, ".codex");
        return new(Path.Combine(claude, "projects"), codex);
    }
}

/// <summary>
/// Brings the agent sessions' usage up to date from their transcripts (TASK-0114). Each transcript is read on from
/// where the last refresh stopped, into per-minute usage (agent_usage). A session's usage is then shared out to the
/// tasks it worked on by time: a task gets what the session used after its previous task's last event, up to the
/// task's own last event, or up to now while the session still works on it. A session's first task gets the work
/// that led to it; what follows its last completed or released task is no task's.
/// </summary>
public static class AgentUsageRefresh
{
    private const string Open = "9999";

    /// <summary>Refreshes the sessions of one task, or of every task when <paramref name="taskId"/> is empty.</summary>
    public static int Refresh(
        HarnessDatabase database,
        PriceTable prices,
        TranscriptRoots roots,
        string taskId = "",
        CancellationToken cancellationToken = default
    )
    {
        var sessions = Sessions(database, taskId, cancellationToken);
        foreach (var session in sessions)
        {
            cancellationToken.ThrowIfCancellationRequested();
            foreach (var path in Paths(database, roots, session))
            {
                ReadFile(database, session, path, cancellationToken);
            }
            Share(database, prices, session, cancellationToken);
        }
        return sessions.Count;
    }

    internal sealed record Session(string Client, string Id, DateTimeOffset Started, DateTimeOffset Ended);

    private static List<Session> Sessions(HarnessDatabase database, string taskId, CancellationToken token) =>
        database.ReadTransaction(
            (c, t) =>
            {
                using var command = Command(
                    c,
                    t,
                    """
                    SELECT client,session_id,MIN(started_at),MAX(ended_at) FROM agent_sessions
                    WHERE $task='' OR (client,session_id) IN (SELECT client,session_id FROM agent_sessions WHERE task_id=$task)
                    GROUP BY client,session_id ORDER BY client,session_id
                    """,
                    ("$task", taskId)
                );
                using var reader = command.ExecuteReader();
                List<Session> sessions = [];
                while (reader.Read())
                {
                    sessions.Add(
                        new(
                            reader.GetString(0),
                            reader.GetString(1),
                            Parse(reader.GetString(2)),
                            Parse(reader.GetString(3))
                        )
                    );
                }
                return sessions;
            },
            token
        );

    /// <summary>
    /// The models the project's transcripts recorded, per client, each with when it was last used (TASK-0142): the
    /// model catalog's 'seen' entries.
    /// </summary>
    public static IReadOnlyList<(string Client, string Model, DateTimeOffset LastUsed)> UsedModels(
        HarnessDatabase database,
        CancellationToken token = default
    ) =>
        database.ReadTransaction(
            (c, t) =>
            {
                using var command = Command(
                    c,
                    t,
                    "SELECT client,model,MAX(last_at) FROM agent_usage WHERE model<>'' GROUP BY client,model"
                );
                using var reader = command.ExecuteReader();
                List<(string, string, DateTimeOffset)> models = [];
                while (reader.Read())
                {
                    models.Add((reader.GetString(0), reader.GetString(1), Parse(reader.GetString(2))));
                }
                return models;
            },
            token
        );

    /// <summary>The session's transcript files: those read before and those found now (the subagents' too).</summary>
    internal static IReadOnlyList<string> Paths(HarnessDatabase database, TranscriptRoots roots, Session session)
    {
        SortedSet<string> paths = new(StringComparer.Ordinal);
        database.ReadTransaction(
            (c, t) =>
            {
                using var command = Command(
                    c,
                    t,
                    "SELECT path FROM agent_transcripts WHERE client=$client AND session_id=$session",
                    ("$client", session.Client),
                    ("$session", session.Id)
                );
                using var reader = command.ExecuteReader();
                while (reader.Read())
                {
                    paths.Add(reader.GetString(0));
                }
                return 0;
            }
        );
        try
        {
            foreach (var path in Locate(roots, session))
            {
                paths.Add(path);
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        { /* A folder that cannot be listed now is listed on the next refresh. */
        }
        return paths.ToArray();
    }

    private static IEnumerable<string> Locate(TranscriptRoots roots, Session session)
    {
        if (
            session.Id.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0
            || session.Id.Contains("..", StringComparison.Ordinal)
        )
        {
            yield break;
        }
        if (session.Client is "claude-code")
        {
            if (!Directory.Exists(roots.ClaudeProjects))
            {
                yield break;
            }
            foreach (var project in Directory.EnumerateDirectories(roots.ClaudeProjects))
            {
                var main = Path.Combine(project, session.Id + ".jsonl");
                var subagents = Path.Combine(project, session.Id, "subagents");
                if (File.Exists(main))
                {
                    yield return main;
                }
                if (Directory.Exists(subagents))
                {
                    foreach (var file in Directory.EnumerateFiles(subagents, "*.jsonl"))
                    {
                        yield return file;
                    }
                }
            }
            yield break;
        }
        // Codex files a rollout under the local date it started: look a day either side of the session's events.
        var pattern = "rollout-*-" + session.Id + ".jsonl";
        var sessions = Path.Combine(roots.CodexHome, "sessions");
        var last = session.Ended.UtcDateTime.Date.AddDays(1);
        var first = session.Started.UtcDateTime.Date.AddDays(-1);
        if (first < last.AddDays(-60))
        {
            first = last.AddDays(-60);
        }
        for (var day = first; day <= last; day = day.AddDays(1))
        {
            var folder = Path.Combine(
                sessions,
                day.ToString("yyyy", CultureInfo.InvariantCulture),
                day.ToString("MM", CultureInfo.InvariantCulture),
                day.ToString("dd", CultureInfo.InvariantCulture)
            );
            if (Directory.Exists(folder))
            {
                foreach (var file in Directory.EnumerateFiles(folder, pattern))
                {
                    yield return file;
                }
            }
        }
        var archived = Path.Combine(roots.CodexHome, "archived_sessions");
        if (Directory.Exists(archived))
        {
            foreach (var file in Directory.EnumerateFiles(archived, pattern))
            {
                yield return file;
            }
        }
    }

    private static void ReadFile(HarnessDatabase database, Session session, string path, CancellationToken token)
    {
        long length;
        try
        {
            length = new FileInfo(path).Length;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return;
        }
        if (!File.Exists(path))
        {
            return;
        }
        var (offset, state) = database.ReadTransaction(
            (c, t) =>
            {
                using var command = Command(
                    c,
                    t,
                    "SELECT offset,state FROM agent_transcripts WHERE client=$client AND session_id=$session AND path=$path",
                    ("$client", session.Client),
                    ("$session", session.Id),
                    ("$path", path)
                );
                using var reader = command.ExecuteReader();
                return reader.Read()
                    ? (reader.GetInt64(0), JsonNode.Parse(reader.GetString(1)) as JsonObject ?? [])
                    : (-1L, new JsonObject());
            },
            token
        );
        if (offset == length)
        {
            return;
        }
        TranscriptRead read;
        try
        {
            read = TranscriptReader.Read(session.Client, session.Id, path, Math.Max(0, offset), state, token);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        { /* A missing or locked file keeps what was read from it; it is read on from there next time. */
            return;
        }
        Dictionary<(string Minute, string Model), Bucket> buckets = [];
        foreach (var entry in read.Entries)
        {
            var key = (entry.At.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm", CultureInfo.InvariantCulture), entry.Model);
            var stamp = HarnessDatabase.FormatTime(entry.At);
            if (!buckets.TryGetValue(key, out var bucket))
            {
                buckets[key] = new(entry.Tokens, entry.ActiveMs, stamp, stamp);
                continue;
            }
            var first = string.CompareOrdinal(stamp, bucket.First) < 0 ? stamp : bucket.First;
            var last = string.CompareOrdinal(stamp, bucket.Last) > 0 ? stamp : bucket.Last;
            buckets[key] = new(bucket.Tokens.Add(entry.Tokens), bucket.ActiveMs + entry.ActiveMs, first, last);
        }
        database.WithTransaction(
            (c, t) =>
            {
                if (read.Rotated)
                {
                    Execute(
                        c,
                        t,
                        "DELETE FROM agent_usage WHERE client=$client AND session_id=$session AND path=$path",
                        ("$client", session.Client),
                        ("$session", session.Id),
                        ("$path", path)
                    );
                }
                foreach (var ((minute, model), bucket) in buckets)
                {
                    Execute(
                        c,
                        t,
                        """
                        INSERT INTO agent_usage(client,session_id,path,minute,model,input,cached_input,cache_write,output,
                            reasoning,active_ms,first_at,last_at)
                        VALUES($client,$session,$path,$minute,$model,$input,$cached,$write,$output,$reasoning,$active,$first,$last)
                        ON CONFLICT(client,session_id,path,minute,model) DO UPDATE SET
                            input=input+excluded.input, cached_input=cached_input+excluded.cached_input,
                            cache_write=cache_write+excluded.cache_write, output=output+excluded.output,
                            reasoning=reasoning+excluded.reasoning, active_ms=active_ms+excluded.active_ms,
                            first_at=min(first_at,excluded.first_at), last_at=max(last_at,excluded.last_at)
                        """,
                        ("$client", session.Client),
                        ("$session", session.Id),
                        ("$path", path),
                        ("$minute", minute),
                        ("$model", model),
                        ("$input", bucket.Tokens.Input),
                        ("$cached", bucket.Tokens.CachedInput),
                        ("$write", bucket.Tokens.CacheWrite),
                        ("$output", bucket.Tokens.Output),
                        ("$reasoning", bucket.Tokens.Reasoning),
                        ("$active", bucket.ActiveMs),
                        ("$first", bucket.First),
                        ("$last", bucket.Last)
                    );
                }
                Execute(
                    c,
                    t,
                    """
                    INSERT INTO agent_transcripts(client,session_id,path,offset,state,read_at)
                    VALUES($client,$session,$path,$offset,$state,$now)
                    ON CONFLICT(client,session_id,path) DO UPDATE SET
                        offset=excluded.offset, state=excluded.state, read_at=excluded.read_at
                    """,
                    ("$client", session.Client),
                    ("$session", session.Id),
                    ("$path", path),
                    ("$offset", read.Offset),
                    ("$state", read.State.ToJsonString()),
                    ("$now", HarnessDatabase.FormatTime(DateTimeOffset.UtcNow))
                );
                return 0;
            },
            token
        );
    }

    private sealed record Bucket(TokenCounts Tokens, long ActiveMs, string First, string Last);

    private sealed record TaskRow(long Id, string Ended, string Status);

    /// <summary>Writes each task's share of the session's usage, priced, into its agent_sessions row.</summary>
    private static void Share(HarnessDatabase database, PriceTable prices, Session session, CancellationToken token) =>
        database.WithTransaction(
            (c, t) =>
            {
                List<TaskRow> rows = [];
                using (
                    var command = Command(
                        c,
                        t,
                        "SELECT id,ended_at,status FROM agent_sessions WHERE client=$client AND session_id=$session ORDER BY ended_at,id",
                        ("$client", session.Client),
                        ("$session", session.Id)
                    )
                )
                using (var reader = command.ExecuteReader())
                {
                    while (reader.Read())
                    {
                        rows.Add(new(reader.GetInt64(0), reader.GetString(1), reader.GetString(2)));
                    }
                }
                var main = MainPath(c, t, session);
                for (var index = 0; index < rows.Count; index++)
                {
                    var from = index is 0 ? "" : rows[index - 1].Ended;
                    var to =
                        index == rows.Count - 1 && rows[index].Status is "working" or "blocked"
                            ? Open
                            : rows[index].Ended;
                    var usage = Usage(c, t, session, from, to, prices);
                    Execute(
                        c,
                        t,
                        """
                        UPDATE agent_sessions SET model=$model,transcript_path=$path,input_tokens=$input,
                            output_tokens=$output,cache_read_tokens=$cached,cache_write_tokens=$write,
                            reasoning_tokens=$reasoning,cost_usd=$cost,active_ms=$active,first_activity=$first,
                            last_activity=$last
                        WHERE id=$id
                        """,
                        ("$model", string.Join(", ", usage.Models)),
                        ("$path", main),
                        ("$input", usage.Tokens.Input),
                        ("$output", usage.Tokens.Output),
                        ("$cached", usage.Tokens.CachedInput),
                        ("$write", usage.Tokens.CacheWrite),
                        ("$reasoning", usage.Tokens.Reasoning),
                        ("$cost", usage.Cost),
                        ("$active", usage.ActiveMs),
                        ("$first", usage.First),
                        ("$last", usage.Last),
                        ("$id", rows[index].Id)
                    );
                }
                return 0;
            },
            token
        );

    private sealed record WindowUsage(
        TokenCounts Tokens,
        IReadOnlyList<string> Models,
        double? Cost,
        long ActiveMs,
        string First,
        string Last
    );

    private static WindowUsage Usage(
        SqliteConnection c,
        SqliteTransaction t,
        Session session,
        string from,
        string to,
        PriceTable prices
    )
    {
        using var command = Command(
            c,
            t,
            """
            SELECT model,SUM(input),SUM(cached_input),SUM(cache_write),SUM(output),SUM(reasoning),SUM(active_ms),
                MIN(first_at),MAX(last_at)
            FROM agent_usage WHERE client=$client AND session_id=$session AND first_at>$from AND first_at<=$to
            GROUP BY model ORDER BY model
            """,
            ("$client", session.Client),
            ("$session", session.Id),
            ("$from", from),
            ("$to", to)
        );
        using var reader = command.ExecuteReader();
        var tokens = TokenCounts.Zero;
        List<string> models = [];
        double? cost = 0;
        long active = 0;
        string first = "",
            last = "";
        while (reader.Read())
        {
            var model = reader.GetString(0);
            var used = new TokenCounts(
                reader.GetInt64(1),
                reader.GetInt64(2),
                reader.GetInt64(3),
                reader.GetInt64(4),
                reader.GetInt64(5)
            );
            tokens = tokens.Add(used);
            active += reader.GetInt64(6);
            first =
                first.Length is 0 || string.CompareOrdinal(reader.GetString(7), first) < 0
                    ? reader.GetString(7)
                    : first;
            last = string.CompareOrdinal(reader.GetString(8), last) > 0 ? reader.GetString(8) : last;
            if (used.Total is 0)
            {
                continue;
            }
            models.Add(model);
            cost = prices.Find(model) is { } price && cost is { } sum ? sum + price.Cost(used) : null;
        }
        return new(
            tokens,
            models,
            tokens.Total is 0 ? null : cost,
            active,
            first.Length is 0 ? "" : MemoryTimestampText(first),
            last.Length is 0 ? "" : MemoryTimestampText(last)
        );
    }

    private static string MainPath(SqliteConnection c, SqliteTransaction t, Session session)
    {
        using var command = Command(
            c,
            t,
            """
            SELECT path FROM agent_transcripts WHERE client=$client AND session_id=$session
            ORDER BY instr(path,'subagents'),path LIMIT 1
            """,
            ("$client", session.Client),
            ("$session", session.Id)
        );
        return command.ExecuteScalar() as string ?? "";
    }

    private static string MemoryTimestampText(string stored) =>
        Harness.Core.Memory.MemoryTimestamp.Parse(stored).ToRfc3339();

    private static DateTimeOffset Parse(string stored) =>
        DateTimeOffset.Parse(stored[..19] + "Z", CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal);

    [System.Diagnostics.CodeAnalysis.SuppressMessage(
        "Security",
        "CA2100:Review SQL queries for security vulnerabilities",
        Justification = "Callers supply constant SQL; all external values use parameters."
    )]
    private static SqliteCommand Command(
        SqliteConnection c,
        SqliteTransaction t,
        string sql,
        params (string Name, object? Value)[] parameters
    )
    {
        var command = c.CreateCommand();
        command.Transaction = t;
        command.CommandText = sql;
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        }
        return command;
    }

    private static void Execute(
        SqliteConnection c,
        SqliteTransaction t,
        string sql,
        params (string Name, object? Value)[] parameters
    )
    {
        using var command = Command(c, t, sql, parameters);
        command.ExecuteNonQuery();
    }
}
