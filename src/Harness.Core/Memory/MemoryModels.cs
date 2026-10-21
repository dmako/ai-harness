namespace Harness.Core.Memory;

public sealed record MemorySource(string Type = "agent", string Ref = "", string By = "unknown");

public sealed record MemoryRecord(
    string Id, string Type, string Content, MemoryScope Scope, MemorySource Source,
    double? Confidence, string SupersededBy, string SupersedeReason,
    MemoryTimestamp? ValidFrom, MemoryTimestamp? ValidUntil, MemoryTimestamp CreatedAt, MemoryTimestamp UpdatedAt)
{
    public IReadOnlyList<string> Overrides { get; init; } = [];

    public bool Current(MemoryTimestamp at) => SupersededBy.Length is 0 &&
        (ValidFrom is null || ValidFrom <= at) && (ValidUntil is null || ValidUntil > at);
}

public sealed record MemoryInput
{
    public string Type { get; init; } = "";
    public string Content { get; init; } = "";
    public MemoryScope? Scope { get; init; }
    public MemorySource Source { get; init; } = new();
    public double? Confidence { get; init; }
    public MemoryTimestamp? ValidFrom { get; init; }
    public MemoryTimestamp? ValidUntil { get; init; }
    public IReadOnlyList<string> Supersedes { get; init; } = [];
    public string SupersedeReason { get; init; } = "";
    public bool Force { get; init; }
}

public sealed record MemoryQuery
{
    public string Text { get; init; } = "";
    public IReadOnlyList<string> Types { get; init; } = [];
    public MemoryScope? Scope { get; init; }
    public string Branch { get; init; } = "";
    public bool IncludeInactive { get; init; }
    public int Limit { get; init; } = 20;
}

public sealed record SimilarMemory(MemoryRecord Memory, double Similarity);
public sealed record MemoryPutResult(string Status, MemoryRecord? Memory = null)
{
    public IReadOnlyList<SimilarMemory> Similar { get; init; } = [];
    public IReadOnlyList<string> Superseded { get; init; } = [];
}

public sealed record MemoryEvent(string Event, string RelatedId, string Actor, string Detail, MemoryTimestamp At);

public sealed record MemoryDetail(MemoryRecord Memory, bool Current, IReadOnlyList<MemoryRecord> History, IReadOnlyList<MemoryEvent> Events);

public sealed record ApplyingMemories(string Scope, string Branch, IReadOnlyList<MemoryRecord> Goals, IReadOnlyList<MemoryRecord> Constraints, IReadOnlyList<MemoryRecord> Decisions);

public interface IMemoryStore
{
    MemoryRecord Get(string id, CancellationToken cancellationToken = default);
    IReadOnlyList<MemoryRecord> Search(MemoryQuery query, CancellationToken cancellationToken = default);
    IReadOnlyList<SimilarMemory> Similar(string type, string content, CancellationToken cancellationToken = default);
}
