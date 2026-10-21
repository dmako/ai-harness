using System.Text.Json.Serialization;
using Harness.Core.Architecture;
using Harness.Core.Providers;

namespace Harness.Core.Findings;

public sealed record FindingMark
{
    [JsonPropertyName("state")] public string State { get; init; } = "";
    [JsonPropertyName("reason"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string Reason { get; init; } = "";
    [JsonPropertyName("level")] public string Level { get; init; } = "";
    [JsonPropertyName("measure")] public int Measure { get; init; } = 0;
    [JsonPropertyName("review_reason"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string ReviewReason { get; init; } = "";
    [JsonPropertyName("memory_id"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string MemoryId { get; init; } = "";
    [JsonPropertyName("task_id"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string TaskId { get; init; } = "";
    [JsonPropertyName("task_project"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string TaskProject { get; init; } = "";
    [JsonPropertyName("marked_by")] public string MarkedBy { get; init; } = "";
    [JsonPropertyName("marked_at")] public string MarkedAt { get; init; } = "";
    [JsonPropertyName("proposed_by"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string ProposedBy { get; init; } = "";
}

public sealed record FindingSnapshot
{
    [JsonPropertyName("id")] public long Id { get; init; } = 0;
    [JsonPropertyName("commit"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string Commit { get; init; } = "";
    [JsonPropertyName("taken_at")] public string TakenAt { get; init; } = "";
    [JsonPropertyName("found")] public int Found { get; init; } = 0;
    [JsonPropertyName("added")] public int Added { get; init; } = 0;
    [JsonPropertyName("changed")] public int Changed { get; init; } = 0;
    [JsonPropertyName("gone")] public int Gone { get; init; } = 0;
}

public sealed record FindingProposal
{
    [JsonPropertyName("id")] public string Id { get; init; } = "";
    [JsonPropertyName("kind")] public string Kind { get; init; } = "";
    [JsonPropertyName("scope")] public string Scope { get; init; } = "";
    [JsonPropertyName("finding_key"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string FindingKey { get; init; } = "";
    [JsonPropertyName("basis")] public string Basis { get; init; } = "";
    [JsonPropertyName("title")] public string Title { get; init; } = "";
    [JsonPropertyName("body"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string Body { get; init; } = "";
    [JsonPropertyName("evidence")] public IReadOnlyList<string> Evidence { get; init; } = [];
    [JsonPropertyName("status")] public string Status { get; init; } = "";
    [JsonPropertyName("proposed_by")] public string ProposedBy { get; init; } = "";
    [JsonPropertyName("proposed_at")] public string ProposedAt { get; init; } = "";
    [JsonPropertyName("decided_by"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string DecidedBy { get; init; } = "";
    [JsonPropertyName("decided_at"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string DecidedAt { get; init; } = "";
    [JsonPropertyName("note"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string Note { get; init; } = "";
    [JsonPropertyName("result"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string Result { get; init; } = "";
}

public sealed record Finding : ArchitectureFinding
{
    public Finding(ArchitectureFinding finding) : base(finding) { }
    [JsonPropertyName("status"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? Status { get; init; }
    [JsonPropertyName("first_seen"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? FirstSeen { get; init; }
    [JsonPropertyName("mark"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public FindingMark? Mark { get; init; }
}
public sealed record FindingTake(FindingSnapshot? Snapshot, IReadOnlyList<string> Review, int Found);
public sealed record FindingGroup(string Where, string Scope, IReadOnlyList<Finding> Findings, FindingSnapshot? Snapshot, IReadOnlyList<WorkspaceMemberEvidence> Members);
public sealed record WorkspaceMemberEvidence(string Name, string Root);
public sealed record FindingsRead(IReadOnlyList<FindingGroup> Groups, string Note = "");
public sealed record FindingProposalRequest(string Kind = "", string Finding = "", string Scope = "", string Title = "", string Reason = "", IReadOnlyList<string>? Evidence = null);
public sealed record FindingProposed(FindingProposal Proposal, bool Existing);
public interface IFindingSession
{
    Task<FindingsRead> ReadFindingsAsync(string scope, CancellationToken token);
    Task<FindingProposed> ProposeFindingAsync(FindingProposalRequest request, string by, CancellationToken token);
    Task<EvidenceSnippet?> ReadFindingSnippetAsync(FindingGroup group, string evidence, int span, CancellationToken token);
}

public sealed record FindingContextGroup(string Where, string Member, IReadOnlyList<Finding> Findings);
