using System.Diagnostics.CodeAnalysis;
using System.Text;
using System.Text.Json;
using Harness.Core.Memory;
using Harness.Core.Tasks;
using Harness.Infrastructure.Storage;
using Microsoft.Data.Sqlite;

namespace Harness.Infrastructure.Tasks;

/// <summary>Task state over a caller-owned database. Fresh reads use deferred read transactions.</summary>
public sealed partial class TaskStore(HarnessDatabase database, TimeProvider? timeProvider = null)
{
    private readonly TimeProvider _time = timeProvider ?? TimeProvider.System;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        PropertyNameCaseInsensitive = true,
    };

    private string Stamp() => HarnessDatabase.FormatTime(_time.GetUtcNow());

    private T Plan<T>(
        Func<SqliteConnection, SqliteTransaction, T> action,
        bool dryRun,
        CancellationToken cancellationToken
    )
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var connection = database.OpenConnection();
        using var transaction = connection.BeginTransaction(deferred: false);
        var result = action(connection, transaction);
        cancellationToken.ThrowIfCancellationRequested();
        if (!dryRun)
        {
            transaction.Commit();
        }

        return result;
    }

    public TaskRecord Get(string id, CancellationToken cancellationToken = default) =>
        Read((c, t) => Get(c, t, id), cancellationToken);

    public IReadOnlyList<TaskRecord> List(TaskFilter? filter = null, CancellationToken cancellationToken = default) =>
        Read((c, t) => List(c, t, filter ?? new()), cancellationToken);

    public TaskRecord? Next(string milestone = "", CancellationToken cancellationToken = default) =>
        Read((c, t) => Next(c, t, milestone), cancellationToken);

    private static TaskRecord? Next(SqliteConnection c, SqliteTransaction t, string milestone)
    {
        using var command = Command(
            c,
            t,
            """
            SELECT id FROM tasks WHERE status='ready' AND ($milestone='' OR milestone=$milestone)
            ORDER BY CASE priority WHEN 'critical' THEN 0 WHEN 'high' THEN 1 WHEN 'medium' THEN 2 ELSE 3 END,ordinal,id LIMIT 1
            """,
            ("$milestone", milestone)
        );
        return command.ExecuteScalar() is string id ? Get(c, t, id) : null;
    }

    public IReadOnlyList<TaskEvent> Events(string id, CancellationToken cancellationToken = default) =>
        database.ReadTransaction(
            (c, t) =>
            {
                using var command = Command(
                    c,
                    t,
                    "SELECT event, COALESCE(from_status,''), COALESCE(to_status,''), actor, COALESCE(detail,''), at FROM task_events WHERE task_id=$id ORDER BY id",
                    ("$id", id)
                );
                using var reader = command.ExecuteReader();
                List<TaskEvent> events = [];
                while (reader.Read())
                {
                    events.Add(
                        new(
                            reader.GetString(0),
                            reader.GetString(1),
                            reader.GetString(2),
                            reader.GetString(3),
                            reader.GetString(4),
                            MemoryTimestamp.Parse(reader.GetString(5))
                        )
                    );
                }

                return (IReadOnlyList<TaskEvent>)events;
            },
            cancellationToken
        );

    private T Read<T>(Func<SqliteConnection, SqliteTransaction, T> read, CancellationToken cancellationToken)
    {
        var stale = false;
        var result = database.ReadTransaction(
            (c, t) =>
            {
                using var command = Command(
                    c,
                    t,
                    """
                    SELECT EXISTS(SELECT 1 FROM tasks WHERE status IN ('claimed','in_progress','review') AND lease_until IS NOT NULL AND lease_until <= $now)
                    OR EXISTS(SELECT 1 FROM tasks t WHERE t.status IN ('backlog','ready') AND (t.status='ready') = EXISTS(
                        SELECT 1 FROM task_dependencies d JOIN tasks dt ON dt.id=d.depends_on WHERE d.task_id=t.id AND dt.status <> 'done'))
                    """,
                    ("$now", Stamp())
                );
                stale =
                    Convert.ToInt64(command.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture)
                        is not 0;
                return stale ? default! : read(c, t);
            },
            cancellationToken
        );
        return !stale
            ? result
            : database.WithTransaction(
                (c, t) =>
                {
                    Refresh(c, t);
                    return read(c, t);
                },
                cancellationToken
            );
    }

    public void Refresh(CancellationToken cancellationToken = default) =>
        database.WithTransaction(
            (c, t) =>
            {
                Refresh(c, t);
                return true;
            },
            cancellationToken
        );

    private void Refresh(SqliteConnection c, SqliteTransaction t)
    {
        var now = Stamp();
        List<(string Id, string Status, string Claimant)> expired = [];
        using (
            var command = Command(
                c,
                t,
                "SELECT id,status,claimant FROM tasks WHERE status IN ('claimed','in_progress','review') AND lease_until IS NOT NULL AND lease_until <= $now",
                ("$now", now)
            )
        )
        using (var reader = command.ExecuteReader())
        {
            while (reader.Read())
            {
                expired.Add((reader.GetString(0), reader.GetString(1), reader.GetString(2)));
            }
        }

        foreach (var item in expired)
        {
            ClearClaim(c, t, item.Id, "ready", now);
            AddEvent(
                c,
                t,
                item.Id,
                "lease_expired",
                item.Status,
                "ready",
                "system",
                "claim by " + item.Claimant + " expired",
                now
            );
        }
        RecomputeReadiness(c, t);
    }

    private List<string> RecomputeReadiness(SqliteConnection c, SqliteTransaction t)
    {
        List<(string Id, string Status, long Open)> states = [];
        using (
            var command = Command(
                c,
                t,
                """
                SELECT t.id,t.status,(SELECT COUNT(*) FROM task_dependencies d JOIN tasks dt ON dt.id=d.depends_on WHERE d.task_id=t.id AND dt.status<>'done')
                FROM tasks t WHERE t.status IN ('backlog','ready') ORDER BY t.ordinal,t.id
                """
            )
        )
        using (var reader = command.ExecuteReader())
        {
            while (reader.Read())
            {
                states.Add((reader.GetString(0), reader.GetString(1), reader.GetInt64(2)));
            }
        }

        List<string> unlocked = [];
        var now = Stamp();
        foreach (var item in states)
        {
            var to = item.Open is 0 ? "ready" : "backlog";
            if (to == item.Status)
            {
                continue;
            }

            Execute(
                c,
                t,
                "UPDATE tasks SET status=$status,updated_at=$now WHERE id=$id",
                ("$status", to),
                ("$now", now),
                ("$id", item.Id)
            );
            AddEvent(
                c,
                t,
                item.Id,
                to is "ready" ? "unlocked" : "relocked",
                item.Status,
                to,
                "system",
                to is "ready"
                    ? "all dependencies done"
                    : string.Create(
                        System.Globalization.CultureInfo.InvariantCulture,
                        $"{item.Open} dependencies not done"
                    ),
                now
            );
            if (to is "ready")
            {
                unlocked.Add(item.Id);
            }
        }
        return unlocked;
    }

    private static TaskRecord[] List(SqliteConnection c, SqliteTransaction t, TaskFilter filter)
    {
        var sql = "SELECT id FROM tasks WHERE 1=1";
        List<(string Name, object? Value)> parameters = [];
        if (filter.Statuses is { Count: > 0 })
        {
            sql += " AND status IN (SELECT value FROM json_each($statuses))";
            parameters.Add(("$statuses", Json(filter.Statuses)));
        }
        if (filter.Milestone.Length is not 0)
        {
            sql += " AND milestone=$milestone";
            parameters.Add(("$milestone", filter.Milestone));
        }
        sql += " ORDER BY ordinal,id";
        if (filter.Limit > 0)
        {
            sql += " LIMIT $limit";
            parameters.Add(("$limit", filter.Limit));
        }
        List<string> ids = [];
        using (var command = Command(c, t, sql, parameters.ToArray()))
        using (var reader = command.ExecuteReader())
        {
            while (reader.Read())
            {
                ids.Add(reader.GetString(0));
            }
        }

        return ids.Select(id => Get(c, t, id)).ToArray();
    }

    private static TaskRecord Get(SqliteConnection c, SqliteTransaction t, string id)
    {
        TaskRecord record;
        using (
            var command = Command(
                c,
                t,
                """
                SELECT id,milestone,title,type,priority,status,goal,scope,validation,notes,
                    blocked_reason,claimant,claim_token,claimed_at,lease_until,completion_summary,completion_commit,completion_checks,completed_by,completed_at,
                    source,ordinal,created_at,updated_at FROM tasks WHERE id=$id
                """,
                ("$id", id)
            )
        )
        using (var r = command.ExecuteReader())
        {
            if (!r.Read())
            {
                throw new KeyNotFoundException($"{id}: task not found");
            }

            string Text(int i) => r.IsDBNull(i) ? "" : r.GetString(i);
            MemoryTimestamp? Time(int i) => r.IsDBNull(i) ? null : MemoryTimestamp.Parse(r.GetString(i));
            record = new()
            {
                Definition = new()
                {
                    Id = Text(0),
                    Milestone = Text(1),
                    Title = Text(2),
                    Type = Text(3),
                    Priority = Text(4),
                    Goal = Text(6),
                    Scope = JsonSerializer.Deserialize<string[]>(Text(7)) ?? [],
                    Validation = JsonSerializer.Deserialize<string[]>(Text(8)) ?? [],
                    Notes = Text(9),
                },
                Status = Text(5),
                BlockedReason = Text(10),
                Claimant = Text(11),
                ClaimToken = Text(12),
                ClaimedAt = Time(13),
                LeaseUntil = Time(14),
                CompletionSummary = Text(15),
                CompletionCommit = Text(16),
                CompletionChecks = r.IsDBNull(17)
                    ? []
                    : JsonSerializer.Deserialize<TaskCheck[]>(Text(17), JsonOptions) ?? [],
                CompletedBy = Text(18),
                CompletedAt = Time(19),
                Source = Text(20),
                Ordinal = r.GetInt32(21),
                CreatedAt = MemoryTimestamp.Parse(Text(22)),
                UpdatedAt = MemoryTimestamp.Parse(Text(23)),
            };
        }
        List<TaskDependency> dependencies = [];
        using (
            var command = Command(
                c,
                t,
                "SELECT d.depends_on,t.status FROM task_dependencies d JOIN tasks t ON t.id=d.depends_on WHERE d.task_id=$id ORDER BY d.depends_on",
                ("$id", id)
            )
        )
        using (var reader = command.ExecuteReader())
        {
            while (reader.Read())
            {
                dependencies.Add(new(reader.GetString(0), reader.GetString(1)));
            }
        }

        List<TaskCriterion> criteria = [];
        using (
            var command = Command(
                c,
                t,
                "SELECT idx,criterion,result,COALESCE(evidence,'') FROM task_acceptance WHERE task_id=$id ORDER BY idx",
                ("$id", id)
            )
        )
        using (var reader = command.ExecuteReader())
        {
            while (reader.Read())
            {
                criteria.Add(new(reader.GetInt32(0), reader.GetString(1), reader.GetString(2), reader.GetString(3)));
            }
        }

        return record with
        {
            Dependencies = dependencies,
            Criteria = criteria,
            Definition = record.Definition with
            {
                DependsOn = dependencies.Select(d => d.Id).ToArray(),
                Acceptance = criteria.Select(x => x.Criterion).ToArray(),
            },
        };
    }

    private static void ClearClaim(SqliteConnection c, SqliteTransaction t, string id, string status, string stamp) =>
        Execute(
            c,
            t,
            "UPDATE tasks SET status=$status,claimant=NULL,claim_token=NULL,claimed_at=NULL,lease_until=NULL,updated_at=$stamp WHERE id=$id",
            ("$status", status),
            ("$stamp", stamp),
            ("$id", id)
        );

    private static void AddEvent(
        SqliteConnection c,
        SqliteTransaction t,
        string id,
        string kind,
        string from,
        string to,
        string actor,
        string detail,
        string stamp
    ) =>
        Execute(
            c,
            t,
            "INSERT INTO task_events(task_id,event,from_status,to_status,actor,detail,at) VALUES($id,$event,$from,$to,$actor,$detail,$stamp)",
            ("$id", id),
            ("$event", kind),
            ("$from", Null(from)),
            ("$to", Null(to)),
            ("$actor", actor),
            ("$detail", Null(CredentialRedactor.Redact(detail))),
            ("$stamp", stamp)
        );

    private static string? Null(string value) => value.Length is 0 ? null : value;

    // Go compares milestone exit JSON byte-for-byte on reimport. Preserve encoding/json's
    // UTF-8 strings, HTML escaping and supplementary runes rather than STJ's ASCII escaping.
    private static string Json(IReadOnlyList<string>? values)
    {
        if (values is null)
        {
            return "null";
        }

        var builder = new StringBuilder("[");
        for (var i = 0; i < values.Count; i++)
        {
            if (i > 0)
            {
                builder.Append(',');
            }

            builder.Append('"');
            foreach (var rune in values[i].EnumerateRunes())
            {
                var escaped = rune.Value switch
                {
                    '"' => "\\\"",
                    '\\' => "\\\\",
                    '\b' => "\\b",
                    '\f' => "\\f",
                    '\n' => "\\n",
                    '\r' => "\\r",
                    '\t' => "\\t",
                    '<' => "\\u003c",
                    '>' => "\\u003e",
                    '&' => "\\u0026",
                    0x2028 => "\\u2028",
                    0x2029 => "\\u2029",
                    < 32 => "\\u" + rune.Value.ToString("x4", System.Globalization.CultureInfo.InvariantCulture),
                    _ => rune.ToString(),
                };
                builder.Append(escaped);
            }
            builder.Append('"');
        }
        return builder.Append(']').ToString();
    }

    [SuppressMessage(
        "Security",
        "CA2100:Review SQL queries for security vulnerabilities",
        Justification = "Private callers supply constant SQL; all external values use parameters."
    )]
    private static SqliteCommand Command(
        SqliteConnection c,
        SqliteTransaction t,
        string sql,
        params (string Name, object? Value)[] parameters
    )
    {
        var command = c.CreateCommand();
        try
        {
            command.Transaction = t;
            command.CommandText = sql;
            foreach (var (name, value) in parameters)
            {
                command.Parameters.AddWithValue(name, value ?? DBNull.Value);
            }

            return command;
        }
        catch
        {
            command.Dispose();
            throw;
        }
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
