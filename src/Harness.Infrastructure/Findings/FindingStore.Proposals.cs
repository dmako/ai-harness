using System.Security.Cryptography;
using System.Text.Json;
using Harness.Core.Findings;

namespace Harness.Infrastructure.Findings;

public sealed partial class FindingStore
{
    private const string ProposalColumns = "id,kind,scope,finding_key,basis,title,body,evidence,status,proposed_by,proposed_at,decided_by,decided_at,note,result";
    private static FindingProposal ReadProposal(Microsoft.Data.Sqlite.SqliteDataReader r) => new()
    {
        Id = r.GetString(0),
        Kind = r.GetString(1),
        Scope = r.GetString(2),
        FindingKey = r.GetString(3),
        Basis = r.GetString(4),
        Title = r.GetString(5),
        Body = r.GetString(6),
        Evidence = JsonSerializer.Deserialize<string[]>(r.GetString(7)) ?? [],
        Status = r.GetString(8),
        ProposedBy = r.GetString(9),
        ProposedAt = r.GetString(10),
        DecidedBy = r.GetString(11),
        DecidedAt = r.GetString(12),
        Note = r.GetString(13),
        Result = r.GetString(14),
    };
    public FindingProposed Propose(FindingProposal p, CancellationToken token = default)
    {
        p = p with { Title = p.Title.Trim(), Body = p.Body.Trim() };
        var error = p switch
        {
            { Kind: not ("task" or "by_design") } => "kind must be task or by_design",
            { Title.Length: 0 } => "a proposal needs a title",
            { Kind: "by_design" } when p.FindingKey.Length is 0 || p.Body.Length is 0 => "a by-design mark needs the finding and why it is intended",
            { FindingKey.Length: 0 } when p.Body.Length is 0 || p.Evidence.Count is 0 => "a task from your own observation needs what should change and why, and the places that show it (evidence)",
            _ => "",
        };
        if (error.Length > 0)
        {
            throw new ArgumentException(error);
        }

        return database.WithTransaction((c, t) =>
        {
            if (p.FindingKey.Length > 0)
            {
                using var existing = Command(c, t, "SELECT " + ProposalColumns + " FROM finding_proposals WHERE status='pending' AND kind=$kind AND scope=$scope AND finding_key=$key ORDER BY proposed_at LIMIT 1", ("$kind", p.Kind), ("$scope", p.Scope), ("$key", p.FindingKey));
                using var r = existing.ExecuteReader();
                if (r.Read())
                {
                    return new FindingProposed(ReadProposal(r), true);
                }
            }
            p = p with { Id = "apr_" + Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(8)), Status = "pending", ProposedAt = Stamp(), DecidedAt = "", DecidedBy = "", Note = "", Result = "" };
            Execute(c, t, """
                INSERT INTO finding_proposals(id,kind,scope,finding_key,basis,title,body,evidence,status,proposed_by,proposed_at)
                VALUES($id,$kind,$scope,$key,$basis,$title,$body,$evidence,'pending',$by,$at)
                """, ("$id", p.Id), ("$kind", p.Kind), ("$scope", p.Scope), ("$key", p.FindingKey), ("$basis", p.Basis), ("$title", p.Title), ("$body", p.Body), ("$evidence", JsonSerializer.Serialize(p.Evidence, Json)), ("$by", p.ProposedBy), ("$at", p.ProposedAt));
            return new FindingProposed(p, false);
        }, token);
    }
    public IReadOnlyList<FindingProposal> Proposals(string status = "pending", CancellationToken token = default) => database.ReadTransaction((c, t) =>
    {
        using var command = Command(c, t, "SELECT " + ProposalColumns + " FROM finding_proposals WHERE ($status='' OR $status='all' OR status=$status) ORDER BY status='pending' DESC,CASE WHEN status='pending' THEN proposed_at END ASC,COALESCE(NULLIF(decided_at,''),proposed_at) DESC", ("$status", status));
        using var r = command.ExecuteReader();
        List<FindingProposal> result = [];
        while (r.Read())
        {
            result.Add(ReadProposal(r));
        }

        return (IReadOnlyList<FindingProposal>)result;
    }, token);
    public FindingProposal GetProposal(string id, CancellationToken token = default) => database.ReadTransaction((c, t) =>
    {
        using var command = Command(c, t, "SELECT " + ProposalColumns + " FROM finding_proposals WHERE id=$id", ("$id", id));
        using var r = command.ExecuteReader();
        return r.Read() ? ReadProposal(r) : throw new KeyNotFoundException(id + ": no such proposal");
    }, token);
    public FindingProposal Decide(string id, string status, string by, string note = "", string result = "", CancellationToken token = default)
    {
        if (status is not ("approved" or "rejected"))
        {
            throw new ArgumentException("a proposal is approved or rejected");
        }

        database.WithTransaction((c, t) =>
        {
            using var command = Command(c, t, "UPDATE finding_proposals SET status=$status,decided_by=$by,decided_at=$at,note=$note,result=$result WHERE id=$id AND status='pending'", ("$status", status), ("$by", by), ("$at", Stamp()), ("$note", note.Trim()), ("$result", result), ("$id", id));
            if (command.ExecuteNonQuery() is 0)
            {
                using var check = Command(c, t, "SELECT status FROM finding_proposals WHERE id=$id", ("$id", id));
                throw check.ExecuteScalar() is null ? new KeyNotFoundException(id + ": no such proposal") : new InvalidOperationException(id + " was already decided");
            }
            return 0;
        }, token);
        return GetProposal(id, token);
    }
}
