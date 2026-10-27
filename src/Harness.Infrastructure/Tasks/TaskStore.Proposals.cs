using System.Security.Cryptography;
using System.Text.Json;
using Harness.Core.Serialization;
using Harness.Core.Tasks;
using Microsoft.Data.Sqlite;

namespace Harness.Infrastructure.Tasks;

public sealed partial class TaskStore
{
    public TaskProposal Propose(
        TaskDefinition definition,
        string reason,
        string by,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentNullException.ThrowIfNull(definition);
        var d = (definition with { Id = "", Status = "" }).Normalize();
        if (d.Title.Length is 0)
        {
            throw new ArgumentException("a task needs a title");
        }

        if (string.IsNullOrWhiteSpace(d.Goal))
        {
            throw new ArgumentException("a task needs its goal: the outcome and why");
        }

        if (d.Acceptance.Count is 0)
        {
            throw new ArgumentException("a task needs acceptance criteria that can be checked");
        }

        try
        {
            (d with { Id = "X-1" }).Validate();
        }
        catch (ArgumentException exception)
        {
            throw new ArgumentException(exception.Message.Replace("X-1: ", "", StringComparison.Ordinal), exception);
        }
        return database.WithTransaction(
            (c, t) =>
            {
                var (existing, _) = LoadGraph(c, t);
                if (d.DependsOn.FirstOrDefault(dep => !existing.Contains(dep)) is { } missing)
                {
                    throw new ArgumentException($"depends on unknown task {missing}");
                }

                var proposal = new TaskProposal(
                    "tpr_" + Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(8)),
                    d with
                    {
                        Blueprint = ResolveBlueprint(c, t, d.Blueprint),
                    },
                    reason.Trim(),
                    "pending",
                    by,
                    Stamp()
                );
                Execute(
                    c,
                    t,
                    "INSERT INTO task_proposals(id,definition,reason,status,proposed_by,proposed_at) VALUES($id,$definition,$reason,'pending',$by,$at)",
                    ("$id", proposal.Id),
                    ("$definition", HarnessJson.Serialize(TaskViews.Definition(proposal.Task), JsonOptions)),
                    ("$reason", proposal.Reason),
                    ("$by", by),
                    ("$at", proposal.ProposedAt)
                );
                return proposal;
            },
            cancellationToken
        );
    }

    public IReadOnlyList<TaskProposal> Proposals(string status = "", CancellationToken cancellationToken = default) =>
        database.ReadTransaction(
            (c, t) =>
            {
                using var command = Command(
                    c,
                    t,
                    """
                    SELECT id,definition,reason,status,proposed_by,proposed_at,decided_by,decided_at,note,result FROM task_proposals
                    WHERE ($status='' OR $status='all' OR status=$status)
                    ORDER BY status='pending' DESC,CASE WHEN status='pending' THEN proposed_at END ASC,COALESCE(NULLIF(decided_at,''),proposed_at) DESC
                    """,
                    ("$status", status)
                );
                using var reader = command.ExecuteReader();
                List<TaskProposal> proposals = [];
                while (reader.Read())
                {
                    proposals.Add(ReadProposal(reader));
                }

                return (IReadOnlyList<TaskProposal>)proposals;
            },
            cancellationToken
        );

    public int PendingProposals(CancellationToken cancellationToken = default) =>
        database.ReadTransaction(
            (c, t) =>
            {
                using var command = Command(c, t, "SELECT COUNT(*) FROM task_proposals WHERE status='pending'");
                return Convert.ToInt32(command.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture);
            },
            cancellationToken
        );

    public TaskProposal GetProposal(string id, CancellationToken cancellationToken = default) =>
        database.ReadTransaction((c, t) => GetProposal(c, t, id), cancellationToken);

    private static TaskProposal GetProposal(SqliteConnection c, SqliteTransaction t, string id)
    {
        using var command = Command(
            c,
            t,
            "SELECT id,definition,reason,status,proposed_by,proposed_at,decided_by,decided_at,note,result FROM task_proposals WHERE id=$id",
            ("$id", id)
        );
        using var reader = command.ExecuteReader();
        return reader.Read() ? ReadProposal(reader) : throw new KeyNotFoundException($"{id}: no such task proposal");
    }

    private static TaskProposal ReadProposal(SqliteDataReader r) =>
        new(
            r.GetString(0),
            ReadDefinition(r.GetString(1)),
            r.GetString(2),
            r.GetString(3),
            r.GetString(4),
            r.GetString(5),
            r.GetString(6),
            r.GetString(7),
            r.GetString(8),
            r.GetString(9)
        );

    private static TaskDefinition ReadDefinition(string json)
    {
        using var document = JsonDocument.Parse(json);
        var d = document.RootElement;
        string Text(string name) => d.TryGetProperty(name, out var value) ? value.GetString() ?? "" : "";
        string[] List(string name) =>
            d.TryGetProperty(name, out var value) && value.ValueKind is JsonValueKind.Array
                ? value.EnumerateArray().Select(x => x.GetString() ?? "").ToArray()
                : [];
        return new()
        {
            Id = Text("id"),
            Title = Text("title"),
            Blueprint = Text("blueprint"),
            Goal = Text("goal"),
            Type = Text("type"),
            Priority = Text("priority"),
            Status = Text("status"),
            Notes = Text("notes"),
            Scope = List("scope"),
            DependsOn = List("depends_on"),
            Acceptance = List("acceptance"),
            Validation = List("validation"),
        };
    }

    public TaskApproval Approve(
        string id,
        TaskDefinition? edit,
        string prefix,
        string by,
        CancellationToken cancellationToken = default
    ) =>
        database.WithTransaction(
            (c, t) =>
            {
                var proposal = GetProposal(c, t, id);
                if (proposal.Status is not "pending")
                {
                    throw new InvalidOperationException($"{id} was already {proposal.Status}");
                }

                var task = Create(
                    c,
                    t,
                    edit ?? proposal.Task,
                    prefix,
                    new(by, "proposal:" + id, $"proposed by {proposal.ProposedBy} ({id}), approved by {by}")
                );
                return new TaskApproval(Decide(c, t, proposal, "approved", by, "", task.Id, Stamp()), task);
            },
            cancellationToken
        );

    public TaskProposal Reject(string id, string note, string by, CancellationToken cancellationToken = default) =>
        database.WithTransaction(
            (c, t) =>
            {
                var proposal = GetProposal(c, t, id);
                if (proposal.Status is not "pending")
                {
                    throw new InvalidOperationException($"{id} was already {proposal.Status}");
                }

                return Decide(c, t, proposal, "rejected", by, note.Trim(), "", Stamp());
            },
            cancellationToken
        );

    private static TaskProposal Decide(
        SqliteConnection c,
        SqliteTransaction t,
        TaskProposal p,
        string status,
        string by,
        string note,
        string result,
        string stamp
    )
    {
        Execute(
            c,
            t,
            "UPDATE task_proposals SET status=$status,decided_by=$by,decided_at=$at,note=$note,result=$result WHERE id=$id",
            ("$status", status),
            ("$by", by),
            ("$at", stamp),
            ("$note", note),
            ("$result", result),
            ("$id", p.Id)
        );
        return p with { Status = status, DecidedBy = by, DecidedAt = stamp, Note = note, Result = result };
    }
}
