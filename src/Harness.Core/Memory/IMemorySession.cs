namespace Harness.Core.Memory;

public sealed record FoundMemory(string Project, MemoryRecord Memory);
public sealed record MemorySearchResult(IReadOnlyList<MemoryRecord> Memories, string Scope = "", string Branch = "", string Note = "", string Hint = "")
{
    public IReadOnlyList<FoundMemory> Elsewhere { get; init; } = [];
}

/// <summary>Project knowledge use cases; storage and wider-level discovery stay behind this boundary.</summary>
public interface IMemorySession
{
    Task<MemoryPutResult> PutMemoryAsync(MemoryInput input, CancellationToken cancellationToken);
    Task<MemoryPutResult> SupersedeMemoryAsync(string id, MemoryInput input, CancellationToken cancellationToken);
    Task<MemoryDetail> ReadMemoryAsync(string id, bool history, CancellationToken cancellationToken);
    Task<MemorySearchResult> SearchMemoryAsync(MemoryQuery query, CancellationToken cancellationToken);
    Task<MemoryProposalResult> ProposeMemoryAsync(MemoryProposalInput input, CancellationToken cancellationToken);
    Task<IReadOnlyList<MemoryProposal>> ListProposalsAsync(string status, int limit, CancellationToken cancellationToken);
    Task<MemoryApprovalResult> ApproveMemoryAsync(string id, MemoryApprovalInput input, CancellationToken cancellationToken);
    Task<MemoryProposal> RejectMemoryAsync(string id, string by, string note, CancellationToken cancellationToken);
}
