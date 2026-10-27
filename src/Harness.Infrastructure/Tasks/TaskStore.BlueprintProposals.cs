using System.Security.Cryptography;
using System.Text.Json;
using Harness.Core.Serialization;
using Harness.Core.Tasks;
using Microsoft.Data.Sqlite;

namespace Harness.Infrastructure.Tasks;

public sealed partial class TaskStore
{
    private const string BlueprintProposalColumns =
        "id,blueprint,definition,reason,status,proposed_by,proposed_at,decided_by,decided_at,note,result";

    /// <summary>
    /// Proposes a new blueprint (<paramref name="blueprint"/> empty) or a restated one; nothing changes until the
    /// author approves it.
    /// </summary>
    public BlueprintProposal ProposeBlueprint(
        string blueprint,
        BlueprintDefinition definition,
        string reason,
        string by,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentNullException.ThrowIfNull(definition);
        blueprint = blueprint.Trim();
        var d = definition with
        {
            Id = blueprint,
            Title = definition.Title.Trim(),
            Goal = definition.Goal.Trim(),
            Exit = (definition.Exit ?? []).Select(e => e.Trim()).Where(e => e.Length is not 0).ToArray(),
            Notes = definition.Notes.Trim(),
        };
        if (d.Title.Length is 0)
        {
            throw new ArgumentException("a blueprint needs a title");
        }

        if (blueprint.Length is 0 && d.Goal.Length is 0)
        {
            throw new ArgumentException("a new blueprint needs its goal: what its tasks are for");
        }

        return database.WithTransaction(
            (c, t) =>
            {
                if (blueprint.Length is not 0 && !BlueprintIds(c, t).Contains(blueprint))
                {
                    throw new ArgumentException($"unknown blueprint {blueprint}");
                }

                var proposal = new BlueprintProposal(
                    "bpr_" + Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(8)),
                    blueprint,
                    d,
                    reason.Trim(),
                    "pending",
                    by,
                    Stamp()
                );
                Execute(
                    c,
                    t,
                    "INSERT INTO blueprint_proposals(id,blueprint,definition,reason,status,proposed_by,proposed_at) VALUES($id,$blueprint,$definition,$reason,'pending',$by,$at)",
                    ("$id", proposal.Id),
                    ("$blueprint", blueprint),
                    (
                        "$definition",
                        HarnessJson.Serialize(TaskViews.BlueprintDefinition(d with { Id = "" }), JsonOptions)
                    ),
                    ("$reason", proposal.Reason),
                    ("$by", by),
                    ("$at", proposal.ProposedAt)
                );
                return proposal;
            },
            cancellationToken
        );
    }

    public IReadOnlyList<BlueprintProposal> BlueprintProposals(
        string status = "",
        CancellationToken cancellationToken = default
    ) =>
        database.ReadTransaction(
            (c, t) =>
            {
                using var command = Command(
                    c,
                    t,
                    $"""
                    SELECT {BlueprintProposalColumns} FROM blueprint_proposals
                    WHERE ($status='' OR $status='all' OR status=$status)
                    ORDER BY status='pending' DESC,CASE WHEN status='pending' THEN proposed_at END ASC,COALESCE(NULLIF(decided_at,''),proposed_at) DESC
                    """,
                    ("$status", status)
                );
                using var reader = command.ExecuteReader();
                List<BlueprintProposal> proposals = [];
                while (reader.Read())
                {
                    proposals.Add(ReadBlueprintProposal(reader));
                }

                return (IReadOnlyList<BlueprintProposal>)proposals;
            },
            cancellationToken
        );

    public BlueprintProposal GetBlueprintProposal(string id, CancellationToken cancellationToken = default) =>
        database.ReadTransaction((c, t) => GetBlueprintProposal(c, t, id), cancellationToken);

    private static BlueprintProposal GetBlueprintProposal(SqliteConnection c, SqliteTransaction t, string id)
    {
        using var command = Command(
            c,
            t,
            $"SELECT {BlueprintProposalColumns} FROM blueprint_proposals WHERE id=$id",
            ("$id", id)
        );
        using var reader = command.ExecuteReader();
        return reader.Read()
            ? ReadBlueprintProposal(reader)
            : throw new KeyNotFoundException($"{id}: no such blueprint proposal");
    }

    private static BlueprintProposal ReadBlueprintProposal(SqliteDataReader r)
    {
        using var document = JsonDocument.Parse(r.GetString(2));
        var d = document.RootElement;
        string Text(string name) => d.TryGetProperty(name, out var value) ? value.GetString() ?? "" : "";
        string[] exit =
            d.TryGetProperty("exit", out var items) && items.ValueKind is JsonValueKind.Array
                ? items.EnumerateArray().Select(x => x.GetString() ?? "").ToArray()
                : [];
        return new(
            r.GetString(0),
            r.GetString(1),
            new(r.GetString(1), Text("title"), Text("goal"), exit, Text("notes")),
            r.GetString(3),
            r.GetString(4),
            r.GetString(5),
            r.GetString(6),
            r.GetString(7),
            r.GetString(8),
            r.GetString(9),
            r.GetString(10)
        );
    }

    /// <summary>
    /// Creates or restates the proposed blueprint, as proposed or as the author restated it. A new one without an
    /// id gets the next free one.
    /// </summary>
    public BlueprintApproval ApproveBlueprint(
        string id,
        BlueprintDefinition? edit,
        string by,
        CancellationToken cancellationToken = default
    ) =>
        database.WithTransaction(
            (c, t) =>
            {
                var proposal = PendingBlueprintProposal(c, t, id);
                var source = "proposal:" + id;
                var d = edit ?? proposal.Definition;
                if (proposal.Blueprint.Length is 0 && d.Id.Trim().Length is 0)
                {
                    d = d with { Id = NextBlueprintId(BlueprintIds(c, t)) };
                }

                var saved = proposal.Blueprint.Length is 0
                    ? CreateBlueprint(c, t, d, source)
                    : EditBlueprint(c, t, proposal.Blueprint, d, source);
                return new BlueprintApproval(
                    DecideBlueprint(c, t, proposal, "approved", by, "", saved.Id, Stamp()),
                    saved
                );
            },
            cancellationToken
        );

    public BlueprintProposal RejectBlueprint(
        string id,
        string note,
        string by,
        CancellationToken cancellationToken = default
    ) =>
        database.WithTransaction(
            (c, t) =>
                DecideBlueprint(c, t, PendingBlueprintProposal(c, t, id), "rejected", by, note.Trim(), "", Stamp()),
            cancellationToken
        );

    private static BlueprintProposal PendingBlueprintProposal(SqliteConnection c, SqliteTransaction t, string id)
    {
        var proposal = GetBlueprintProposal(c, t, id);
        return proposal.Status is "pending"
            ? proposal
            : throw new InvalidOperationException($"{id} was already {proposal.Status}");
    }

    private static BlueprintProposal DecideBlueprint(
        SqliteConnection c,
        SqliteTransaction t,
        BlueprintProposal p,
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
            "UPDATE blueprint_proposals SET status=$status,decided_by=$by,decided_at=$at,note=$note,result=$result WHERE id=$id",
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
