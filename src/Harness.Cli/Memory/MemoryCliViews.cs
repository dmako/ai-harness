using Harness.Core.Memory;
using Harness.Core.Serialization;

namespace Harness.Cli.Memory;

// The Go proposals CLI serializes domain structs (PascalCase), unlike the MCP view DTOs.
internal static class MemoryCliViews
{
    public static object Proposal(MemoryProposal proposal) =>
        new JsonObjectMap
        {
            ["ID"] = proposal.Id,
            ["Type"] = proposal.Type,
            ["Content"] = proposal.Content,
            ["Scope"] = new JsonObjectMap { ["Kind"] = proposal.Scope.Kind, ["Value"] = proposal.Scope.Value },
            ["Source"] = proposal.Source,
            ["Rationale"] = proposal.Rationale,
            ["Supersedes"] = proposal.Supersedes,
            ["Status"] = proposal.Status,
            ["DecidedBy"] = proposal.DecidedBy,
            ["DecidedAt"] = proposal.DecidedAt?.ToRfc3339Nano(),
            ["DecisionNote"] = proposal.DecisionNote,
            ["MemoryID"] = proposal.MemoryId,
            ["CreatedAt"] = proposal.CreatedAt.ToRfc3339Nano(),
        };

    public static object? Similar(IReadOnlyList<SimilarMemory> similar) =>
        similar.Count is 0
            ? null
            : similar
                .Select(item => new JsonObjectMap
                {
                    ["Memory"] = Memory(item.Memory),
                    ["Similarity"] = item.Similarity,
                })
                .ToArray();

    public static object Approval(MemoryApprovalResult result) =>
        new JsonObjectMap
        {
            ["Status"] = result.Status,
            ["Proposal"] = Proposal(result.Proposal),
            ["Memory"] = result.Memory is null ? null : Memory(result.Memory),
            ["Similar"] = Similar(result.Similar),
        };

    private static JsonObjectMap Memory(MemoryRecord memory) =>
        new JsonObjectMap
        {
            ["ID"] = memory.Id,
            ["Type"] = memory.Type,
            ["Content"] = memory.Content,
            ["Scope"] = new JsonObjectMap { ["Kind"] = memory.Scope.Kind, ["Value"] = memory.Scope.Value },
            ["Source"] = memory.Source,
            ["Confidence"] = memory.Confidence,
            ["SupersededBy"] = memory.SupersededBy,
            ["SupersedeReason"] = memory.SupersedeReason,
            ["ValidFrom"] = memory.ValidFrom?.ToRfc3339Nano(),
            ["ValidUntil"] = memory.ValidUntil?.ToRfc3339Nano(),
            ["CreatedAt"] = memory.CreatedAt.ToRfc3339Nano(),
            ["UpdatedAt"] = memory.UpdatedAt.ToRfc3339Nano(),
            ["Overrides"] = memory.Overrides.Count is 0 ? null : memory.Overrides,
        };
}
