using Harness.Core.Memory;

namespace Harness.Cli.Memory;

// The Go proposals CLI serializes domain structs (PascalCase), unlike the MCP view DTOs.
internal static class MemoryCliViews
{
    public static object Proposal(MemoryProposal proposal) =>
        new
        {
            ID = proposal.Id,
            proposal.Type,
            proposal.Content,
            Scope = new { proposal.Scope.Kind, proposal.Scope.Value },
            proposal.Source,
            proposal.Rationale,
            proposal.Supersedes,
            proposal.Status,
            proposal.DecidedBy,
            DecidedAt = proposal.DecidedAt?.ToRfc3339Nano(),
            proposal.DecisionNote,
            MemoryID = proposal.MemoryId,
            CreatedAt = proposal.CreatedAt.ToRfc3339Nano(),
        };

    public static object? Similar(IReadOnlyList<SimilarMemory> similar) =>
        similar.Count is 0
            ? null
            : similar.Select(item => new { Memory = Memory(item.Memory), item.Similarity }).ToArray();

    public static object Approval(MemoryApprovalResult result) =>
        new
        {
            result.Status,
            Proposal = Proposal(result.Proposal),
            Memory = result.Memory is null ? null : Memory(result.Memory),
            Similar = Similar(result.Similar),
        };

    private static object Memory(MemoryRecord memory) =>
        new
        {
            ID = memory.Id,
            memory.Type,
            memory.Content,
            Scope = new { memory.Scope.Kind, memory.Scope.Value },
            memory.Source,
            memory.Confidence,
            memory.SupersededBy,
            memory.SupersedeReason,
            ValidFrom = memory.ValidFrom?.ToRfc3339Nano(),
            ValidUntil = memory.ValidUntil?.ToRfc3339Nano(),
            CreatedAt = memory.CreatedAt.ToRfc3339Nano(),
            UpdatedAt = memory.UpdatedAt.ToRfc3339Nano(),
            Overrides = memory.Overrides.Count is 0 ? null : memory.Overrides,
        };
}
