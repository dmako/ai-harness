using System.Security.Cryptography;
using Harness.Core.Memory;
using Microsoft.Data.Sqlite;

namespace Harness.Infrastructure.Memory;

public sealed partial class MemoryStore
{
    private const string ProposalColumns = "id, type, content, scope_kind, scope_value, source_type, source_ref, proposed_by, rationale, supersedes, status, decided_by, decided_at, decision_note, memory_id, created_at";

    public MemoryProposalResult Propose(MemoryProposalInput input, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        var normalized = MemoryText.Validate(new MemoryInput { Type = input.Type, Content = input.Content, Scope = input.Scope, Source = input.Source ?? new MemorySource() });
        var rationale = CredentialRedactor.Redact(input.Rationale.Trim());
        var hash = MemoryText.Hash(normalized.Content);
        return database.WithTransaction((connection, transaction) =>
        {
            if (input.Supersedes.Length is not 0)
            {
                var old = Get(connection, transaction, input.Supersedes);
                if (old.SupersededBy.Length is not 0)
                    throw new InvalidOperationException($"{old.Id} is already superseded by {old.SupersededBy}");
            }

            var parameters = new (string Name, object? Value)[] { ("$type", normalized.Type), ("$kind", normalized.Scope!.Kind), ("$value", normalized.Scope.Value), ("$hash", hash) };
            using (var duplicate = Command(connection, transaction, "SELECT id FROM memories WHERE type=$type AND scope_kind=$kind AND scope_value=$value AND content_hash=$hash AND superseded_by IS NULL", parameters))
            {
                if (duplicate.ExecuteScalar() is string memoryId)
                    return new MemoryProposalResult("already_memory", Memory: Get(connection, transaction, memoryId));
            }

            using (var duplicate = Command(connection, transaction, "SELECT id FROM memory_proposals WHERE type=$type AND scope_kind=$kind AND scope_value=$value AND content_hash=$hash AND status='pending'", parameters))
            {
                if (duplicate.ExecuteScalar() is string proposalId)
                    return new MemoryProposalResult("already_proposed", GetProposal(connection, transaction, proposalId));
            }

            var now = Now();
            var related = FindSimilar(connection, transaction, normalized.Type, normalized.Content, now, cancellationToken);
            var id = "prop_" + Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(8));
            Execute(connection, transaction, """
                INSERT INTO memory_proposals(id, type, content, content_hash, scope_kind, scope_value, source_type, source_ref, proposed_by, rationale, supersedes, status, created_at)
                VALUES ($id, $type, $content, $hash, $kind, $value, $source, $ref, $by, $rationale, $supersedes, 'pending', $stamp)
                """, ("$id", id), ("$type", normalized.Type), ("$content", normalized.Content), ("$hash", hash), ("$kind", normalized.Scope.Kind), ("$value", normalized.Scope.Value),
                ("$source", normalized.Source.Type), ("$ref", normalized.Source.Ref), ("$by", normalized.Source.By), ("$rationale", NullIfEmpty(rationale)), ("$supersedes", NullIfEmpty(input.Supersedes)), ("$stamp", now.ToString()));
            return new MemoryProposalResult("proposed", GetProposal(connection, transaction, id)) { Related = related };
        }, cancellationToken);
    }

    public IReadOnlyList<MemoryProposal> Proposals(string status = "", int limit = 50, CancellationToken cancellationToken = default)
    {
        if (status.Length is 0)
            status = "pending";
        if (status is not ("pending" or "approved" or "rejected" or "all"))
            throw new ArgumentException($"status must be pending, approved, rejected or all, got \"{status}\"");
        cancellationToken.ThrowIfCancellationRequested();
        using var connection = database.OpenConnection();
        using var command = Command(connection, null, "SELECT " + ProposalColumns + " FROM memory_proposals" + (status is "all" ? "" : " WHERE status=$status") + " ORDER BY created_at, id LIMIT $limit",
            ("$status", status), ("$limit", limit <= 0 ? 50 : limit));
        using var reader = command.ExecuteReader();
        List<MemoryProposal> output = [];
        while (reader.Read())
        {
            cancellationToken.ThrowIfCancellationRequested();
            output.Add(ReadProposal(reader));
        }
        return output;
    }

    public MemoryPending Pending(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var connection = database.OpenConnection();
        using var command = Command(connection, null, "SELECT COUNT(*), MIN(created_at) FROM memory_proposals WHERE status='pending'");
        using var reader = command.ExecuteReader();
        reader.Read();
        return new MemoryPending(reader.GetInt32(0), reader.IsDBNull(1) ? null : ParseTime(reader.GetString(1)));
    }

    public MemoryProposalDetail GetProposal(string id, CancellationToken cancellationToken = default) =>
        database.ReadTransaction((connection, transaction) =>
        {
            var proposal = GetProposal(connection, transaction, id);
            return new MemoryProposalDetail(proposal, proposal.Status is "pending" ? FindSimilar(connection, transaction, proposal.Type, proposal.Content, Now(), cancellationToken) : []);
        }, cancellationToken);

    public MemoryApprovalResult Approve(string id, MemoryApprovalInput input, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        return database.WithTransaction((connection, transaction) =>
        {
            var proposal = GetProposal(connection, transaction, id);
            EnsurePending(proposal);
            var supersedes = input.Supersedes;
            if (supersedes.Count is 0 && proposal.Supersedes.Length is not 0 && !input.KeepBoth)
                supersedes = [proposal.Supersedes];
            var normalized = MemoryText.Validate(new MemoryInput
            {
                Type = input.Type.Length is 0 ? proposal.Type : input.Type,
                Content = string.IsNullOrWhiteSpace(input.Content) ? proposal.Content : input.Content,
                Scope = input.Scope ?? proposal.Scope,
                Source = proposal.Source,
                Supersedes = supersedes,
                Force = input.KeepBoth,
                SupersedeReason = supersedes.Count > 0 && input.Reason.Length is 0 ? "approved proposal " + id : input.Reason,
            });
            var result = Put(connection, transaction, normalized, cancellationToken);
            if (result.Status is "needs_decision")
                return new MemoryApprovalResult(result.Status, proposal) { Similar = result.Similar };
            var by = input.By.Trim();
            Execute(connection, transaction, "UPDATE memory_proposals SET status='approved', decided_by=$by, decided_at=$stamp, decision_note=$note, memory_id=$memory WHERE id=$id",
                ("$by", by.Length is 0 ? "unknown" : by), ("$stamp", Now().ToString()), ("$note", NullIfEmpty(input.Note.Trim())), ("$memory", result.Memory!.Id), ("$id", id));
            return new MemoryApprovalResult(result.Status, GetProposal(connection, transaction, id), result.Memory);
        }, cancellationToken);
    }

    public MemoryProposal Reject(string id, string by, string note, CancellationToken cancellationToken = default) =>
        database.WithTransaction((connection, transaction) =>
        {
            var proposal = GetProposal(connection, transaction, id);
            EnsurePending(proposal);
            Execute(connection, transaction, "UPDATE memory_proposals SET status='rejected', decided_by=$by, decided_at=$stamp, decision_note=$note WHERE id=$id",
                ("$by", string.IsNullOrWhiteSpace(by) ? "unknown" : by), ("$stamp", Now().ToString()), ("$note", NullIfEmpty(note.Trim())), ("$id", id));
            return GetProposal(connection, transaction, id);
        }, cancellationToken);

    private static void EnsurePending(MemoryProposal proposal)
    {
        if (proposal.Status is not "pending")
            throw new InvalidOperationException($"proposal {proposal.Id} is already {proposal.Status}");
    }

    private static MemoryProposal GetProposal(SqliteConnection connection, SqliteTransaction transaction, string id)
    {
        using var command = Command(connection, transaction, "SELECT " + ProposalColumns + " FROM memory_proposals WHERE id=$id", ("$id", id));
        using var reader = command.ExecuteReader();
        return reader.Read() ? ReadProposal(reader) : throw new KeyNotFoundException($"{id}: proposal not found");
    }

    private static MemoryProposal ReadProposal(SqliteDataReader reader) => new(reader.GetString(0), reader.GetString(1), reader.GetString(2), new MemoryScope(reader.GetString(3), reader.GetString(4)),
        new MemorySource(reader.GetString(5), reader.GetString(6), reader.GetString(7)), Optional(reader, 8), Optional(reader, 9), reader.GetString(10), Optional(reader, 11),
        reader.IsDBNull(12) ? null : ParseTime(reader.GetString(12)), Optional(reader, 13), Optional(reader, 14), ParseTime(reader.GetString(15)) ?? default);

    private static string Optional(SqliteDataReader reader, int ordinal) => reader.IsDBNull(ordinal) ? "" : reader.GetString(ordinal);
}
