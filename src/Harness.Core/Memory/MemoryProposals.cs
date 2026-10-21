namespace Harness.Core.Memory;

public sealed record MemoryProposal(string Id, string Type, string Content, MemoryScope Scope, MemorySource Source,
    string Rationale, string Supersedes, string Status, string DecidedBy, MemoryTimestamp? DecidedAt,
    string DecisionNote, string MemoryId, MemoryTimestamp CreatedAt);

public sealed record MemoryProposalInput(string Type, string Content, MemoryScope? Scope = null,
    MemorySource? Source = null, string Rationale = "", string Supersedes = "");

public sealed record MemoryProposalResult(string Status, MemoryProposal? Proposal = null, MemoryRecord? Memory = null)
{
    public IReadOnlyList<SimilarMemory> Related { get; init; } = [];
}

public sealed record MemoryApprovalInput
{
    public string Type { get; init; } = "";
    public string Content { get; init; } = "";
    public MemoryScope? Scope { get; init; }
    public IReadOnlyList<string> Supersedes { get; init; } = [];
    public string Reason { get; init; } = "";
    public bool KeepBoth { get; init; }
    public string By { get; init; } = "";
    public string Note { get; init; } = "";
}

public sealed record MemoryApprovalResult(string Status, MemoryProposal Proposal, MemoryRecord? Memory = null)
{
    public IReadOnlyList<SimilarMemory> Similar { get; init; } = [];
}

public sealed record MemoryProposalDetail(MemoryProposal Proposal, IReadOnlyList<SimilarMemory> Related);
public sealed record MemoryPending(int Count, MemoryTimestamp? Oldest);
