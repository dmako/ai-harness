using System.Globalization;
using System.Text.RegularExpressions;
using Harness.Core.Memory;
using Harness.Core.Tasks;
using Microsoft.Data.Sqlite;

namespace Harness.Infrastructure.Tasks;

public sealed partial class TaskStore
{
    public TaskRecord Create(
        TaskDefinition definition,
        string prefix,
        TaskOrigin origin,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentNullException.ThrowIfNull(origin);
        return database.WithTransaction((c, t) => Create(c, t, definition, prefix, origin), cancellationToken);
    }

    private TaskRecord Create(
        SqliteConnection c,
        SqliteTransaction t,
        TaskDefinition definition,
        string prefix,
        TaskOrigin origin
    )
    {
        var d = (
            definition with
            {
                Id = string.IsNullOrWhiteSpace(definition.Id)
                    ? NextId(c, t, prefix)
                    : definition.Id.Trim().ToUpperInvariant(),
                Status = "",
            }
        ).Normalize();
        d.Validate();
        var (existing, _) = LoadGraph(c, t);
        if (existing.Contains(d.Id))
        {
            throw new InvalidOperationException($"{d.Id}: task already exists");
        }

        if (d.DependsOn.FirstOrDefault(dep => !existing.Contains(dep)) is { } missing)
        {
            throw new ArgumentException($"{d.Id} depends on unknown task {missing}");
        }

        using var ordinal = Command(c, t, "SELECT COALESCE(MAX(ordinal),-1)+1 FROM tasks");
        Insert(
            c,
            t,
            d,
            Convert.ToInt32(ordinal.ExecuteScalar(), CultureInfo.InvariantCulture),
            origin.Source,
            Stamp(),
            "created",
            origin.By,
            origin.Detail
        );
        ReplaceDependencies(c, t, d);
        RecomputeReadiness(c, t);
        return Get(c, t, d.Id);
    }

    public string NextId(string prefix = "", CancellationToken cancellationToken = default) =>
        database.WithTransaction((c, t) => NextId(c, t, prefix), cancellationToken);

    private static string NextId(SqliteConnection c, SqliteTransaction t, string prefix)
    {
        prefix = prefix.Trim();
        if (prefix.EndsWith('-'))
        {
            prefix = prefix[..^1];
        }

        prefix = prefix.ToUpperInvariant();
        if (prefix.Length is 0)
        {
            using var latest = Command(
                c,
                t,
                "SELECT id FROM tasks WHERE source NOT LIKE 'finding:%' AND source NOT LIKE 'observation:%' ORDER BY created_at DESC,ordinal DESC,id DESC LIMIT 1"
            );
            prefix = latest.ExecuteScalar() is string id ? id.Split('-')[0] : "TASK";
        }
        if (!PrefixPattern.IsMatch(prefix))
        {
            throw new ArgumentException(
                $"invalid id prefix {TaskWorkflow.Quote(prefix)} (want capital letters and digits, e.g. TASK)"
            );
        }

        using var command = Command(c, t, "SELECT id FROM tasks WHERE id LIKE $prefix", ("$prefix", prefix + "-%"));
        using var reader = command.ExecuteReader();
        long max = 0;
        var width = 0;
        while (reader.Read())
        {
            var digits = reader.GetString(0)[(prefix.Length + 1)..];
            if (!long.TryParse(digits, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var number))
            {
                continue;
            }

            max = Math.Max(max, number);
            width = Math.Max(width, digits.Length);
        }
        return prefix
            + "-"
            + checked(max + 1).ToString(
                "D" + (width is 0 ? 4 : width).ToString(CultureInfo.InvariantCulture),
                CultureInfo.InvariantCulture
            );
    }

    public TaskRecord Block(string id, TaskActor actor, string reason, CancellationToken cancellationToken = default) =>
        SetAside(id, actor, "blocked", reason, cancellationToken);

    public TaskRecord Defer(string id, TaskActor actor, string reason, CancellationToken cancellationToken = default) =>
        SetAside(id, actor, "deferred", reason, cancellationToken);

    private TaskRecord SetAside(
        string id,
        TaskActor actor,
        string status,
        string reason,
        CancellationToken cancellationToken
    ) =>
        database.WithTransaction(
            (c, t) =>
            {
                Refresh(c, t);
                var current = Get(c, t, id);
                if (TaskWorkflow.HoldsClaim(current.Status))
                {
                    Authorize(current, actor);
                }

                Transition(c, t, current, status, actor, reason, Stamp());
                return Get(c, t, id);
            },
            cancellationToken
        );

    public TaskRecord Unblock(
        string id,
        TaskActor actor,
        string note = "",
        CancellationToken cancellationToken = default
    ) =>
        database.WithTransaction(
            (c, t) =>
            {
                var current = Get(c, t, id);
                if (current.Status is not ("blocked" or "deferred"))
                {
                    throw new InvalidOperationException($"{id}: not blocked or deferred (status {current.Status})");
                }

                var stamp = Stamp();
                Execute(
                    c,
                    t,
                    "UPDATE tasks SET status='backlog',blocked_reason=NULL,updated_at=$stamp WHERE id=$id",
                    ("$stamp", stamp),
                    ("$id", id)
                );
                AddEvent(
                    c,
                    t,
                    id,
                    current.Status is "blocked" ? "unblocked" : "resumed",
                    current.Status,
                    "backlog",
                    actor.AuditName,
                    note,
                    stamp
                );
                RecomputeReadiness(c, t);
                return Get(c, t, id);
            },
            cancellationToken
        );

    private static void Authorize(TaskRecord task, TaskActor actor)
    {
        if (actor.Privileged)
        {
            return;
        }

        if (!TaskWorkflow.HoldsClaim(task.Status))
        {
            throw new InvalidOperationException($"{task.Id}: no active claim (status {task.Status}); claim it first");
        }

        if (actor.Token.Length is 0 || actor.Token != task.ClaimToken)
        {
            throw new InvalidOperationException($"{task.Id}: claim token does not match the task's current claim");
        }
    }

    private void Transition(
        SqliteConnection c,
        SqliteTransaction t,
        TaskRecord current,
        string to,
        TaskActor actor,
        string reason,
        string stamp
    )
    {
        if (to is "done")
        {
            throw new InvalidOperationException($"{current.Id}: use complete to finish a task (it records evidence)");
        }

        if (to is "claimed")
        {
            throw new InvalidOperationException($"{current.Id}: use claim to claim a task");
        }

        if (!TaskWorkflow.CanTransition(current.Status, to))
        {
            throw new InvalidOperationException(
                $"{current.Id}: status transition not allowed: {current.Status} -> {to}"
            );
        }

        switch (to)
        {
            case "ready" or "backlog":
                ClearClaim(c, t, current.Id, to, stamp);
                RecomputeReadiness(c, t);
                break;
            case "blocked" or "deferred":
                if (string.IsNullOrWhiteSpace(reason))
                {
                    throw new ArgumentException(
                        $"{current.Id}: {(to is "blocked" ? "blocking" : "deferring")} requires a reason"
                    );
                }

                ClearClaim(c, t, current.Id, to, stamp);
                Execute(
                    c,
                    t,
                    "UPDATE tasks SET blocked_reason=$reason WHERE id=$id",
                    ("$reason", CredentialRedactor.Redact(reason)),
                    ("$id", current.Id)
                );
                break;
            default:
                Execute(
                    c,
                    t,
                    "UPDATE tasks SET status=$status,updated_at=$stamp WHERE id=$id",
                    ("$status", to),
                    ("$stamp", stamp),
                    ("$id", current.Id)
                );
                break;
        }
        AddEvent(c, t, current.Id, "status", current.Status, to, actor.AuditName, reason, stamp);
    }

    [GeneratedRegex(@"\A[A-Z][A-Z0-9]*\z", RegexOptions.CultureInvariant, 1000)]
    private static partial Regex PrefixPattern { get; }
}
