using System.Text.Json.Serialization;

namespace Harness.Ui.Api;

// The knowledge area (UiReadService.Project, Machine, Writes; MemoryViews): a project's memories
// (GET /api/projects/{id}/memories?type=…&limit=…&levels=1[&q=…][&inactive=1][&shared=removed]), one memory with its
// history and trail (memories/{mid}), what applies to a scope (applies?scope=…), the memory proposals
// (proposals?status=…), the wider levels (GET /api/knowledge?level=user|workspace:<name>, /api/knowledge/memories/{mid}
// ?level=…) and every write on them. Required members are always in the server's answer.

/// <summary>A memory: its statement, type and scope, who recorded it and how it relates to others.</summary>
public sealed class Memory
{
    public required string Id { get; init; }

    /// <summary>goal, constraint, decision, convention, warning, fact or open_question.</summary>
    public required string Type { get; init; }

    public required string Content { get; init; }

    /// <summary>project, path:…, subsystem:…, branch:…, or a level: user or workspace:&lt;name&gt;.</summary>
    public required string Scope { get; init; }

    /// <summary>user (stated by the author) or agent.</summary>
    public required string SourceType { get; init; }

    public string? SourceRef { get; init; }
    public string? RecordedBy { get; init; }
    public required string CreatedAt { get; init; }
    public string? SupersededBy { get; init; }
    public string? SupersedeReason { get; init; }
    public double? Confidence { get; init; }
    public string? ValidFrom { get; init; }
    public string? ValidUntil { get; init; }

    /// <summary>"" local, project (in .harness/knowledge.toml), removed (a Git change took it out).</summary>
    public string? Shared { get; init; }

    /// <summary>The wider levels' memories this project's own takes precedence over.</summary>
    public IReadOnlyList<string> Overrides { get; init; } = [];

    /// <summary>The projects whose own memory takes precedence over this level's.</summary>
    public IReadOnlyList<string> OverriddenIn { get; init; } = [];
}

/// <summary>GET …/memories: the memories the query matched.</summary>
public sealed class MemoryList
{
    public required IReadOnlyList<Memory> Memories { get; init; }
}

/// <summary>GET /api/knowledge?level=…: a level's memories.</summary>
public sealed class LevelKnowledge
{
    public required string Level { get; init; }
    public required IReadOnlyList<Memory> Memories { get; init; }
}

/// <summary>GET …/memories/{mid}: a memory, its versions (oldest first) and its audit trail.</summary>
public sealed class MemoryDetail
{
    public required Memory Memory { get; init; }
    public bool Current { get; init; }
    public IReadOnlyList<Memory> History { get; init; } = [];
    public IReadOnlyList<MemoryEvent> Events { get; init; } = [];
}

public sealed class MemoryEvent
{
    public required string Event { get; init; }
    public required string Actor { get; init; }
    public required string At { get; init; }
    public string? RelatedId { get; init; }
    public string? Detail { get; init; }
}

/// <summary>GET …/applies?scope=…: what an agent is told about a scope on the project's branch.</summary>
public sealed class Applies
{
    public required string Scope { get; init; }
    public string? Branch { get; init; }
    public required IReadOnlyList<Memory> Goals { get; init; }
    public required IReadOnlyList<Memory> Constraints { get; init; }
    public required IReadOnlyList<Memory> Decisions { get; init; }
}

/// <summary>A current memory a statement resembles, and how much.</summary>
public sealed class SimilarMemory
{
    public required Memory Memory { get; init; }
    public required double Similarity { get; init; }
}

/// <summary>A memory an agent proposed: not active until the author approves it.</summary>
public sealed class MemoryProposal
{
    public required string Id { get; init; }
    public required string Type { get; init; }
    public required string Content { get; init; }
    public required string Scope { get; init; }
    public string? SourceRef { get; init; }
    public required string ProposedBy { get; init; }

    /// <summary>pending, approved or rejected.</summary>
    public required string Status { get; init; }

    public required string CreatedAt { get; init; }
    public string? Rationale { get; init; }
    public string? Supersedes { get; init; }
    public string? DecidedBy { get; init; }
    public string? DecisionNote { get; init; }
    public string? MemoryId { get; init; }

    /// <summary>Current memories it resembles (approving asks whether to replace one or keep both).</summary>
    public IReadOnlyList<SimilarMemory> Similar { get; init; } = [];
}

public sealed class MemoryProposalList
{
    public required IReadOnlyList<MemoryProposal> Proposals { get; init; }
}

/// <summary>POST …/memories (a project scope) or /api/knowledge/memories (with its level): a statement of the author's.</summary>
public sealed class MemoryAdd
{
    public required string Type { get; init; }
    public required string Content { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Scope { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Level { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? SourceRef { get; init; }
}

/// <summary>What adding a memory did: recorded (memory), or needs_decision with the memories it resembles.</summary>
public sealed class MemoryAdded
{
    public required string Status { get; init; }
    public Memory? Memory { get; init; }
    public IReadOnlyList<Memory>? Similar { get; init; }
}

/// <summary>POST …/memories/{mid}/supersede (content and reason) or retire (reason); a level's carries it.</summary>
public sealed class MemoryChange
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Content { get; init; }

    public required string Reason { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Level { get; init; }
}

/// <summary>POST …/memories/{mid}/promote: to user or workspace:&lt;name&gt;; a dry run only says where it holds.</summary>
public sealed class Promotion
{
    public required string To { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? DryRun { get; init; }
}

/// <summary>Another project's memory that already says the same.</summary>
public sealed class FoundMemory
{
    public required string Project { get; init; }
    public required Memory Memory { get; init; }
}

/// <summary>What promoting did or would do: where it already holds, and the level's new memory.</summary>
public sealed class Promoted
{
    /// <summary>would_create (a dry run), recorded, or needs_decision (the level holds something similar).</summary>
    public required string Status { get; init; }

    public required string Level { get; init; }
    public required IReadOnlyList<FoundMemory> HoldsIn { get; init; }
    public bool Retired { get; init; }
    public Memory? Memory { get; init; }
    public IReadOnlyList<string> SimilarAtLevel { get; init; } = [];
}

/// <summary>What sharing or withdrawing did, and a warning when Git ignores the shared file.</summary>
public sealed class ShareResult
{
    public required Memory Memory { get; init; }
    public required string Shared { get; init; }
    public string? Warning { get; init; }
    public string? Fix { get; init; }
}

/// <summary>POST proposals/{pid}/approve: as proposed, edited, replacing current memories or keeping both.</summary>
public sealed class ProposalApproval
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Content { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<string>? Supersedes { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? KeepBoth { get; init; }
}

/// <summary>What approving did: approved (with the memory), or needs_decision with the memories it resembles.</summary>
public sealed class ProposalApproved
{
    public required string Status { get; init; }
    public IReadOnlyList<SimilarMemory>? Similar { get; init; }
}
