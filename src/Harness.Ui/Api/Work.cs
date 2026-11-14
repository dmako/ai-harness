using System.Text.Json.Serialization;

namespace Harness.Ui.Api;

// The work area (UiReadService.Project, Writes, Reviews; TaskViews): GET /api/projects/{id}/blueprints,
// tasks-next-id, blueprints-next-id, tasks/{task}/events, tasks/{task}/sessions, task-proposals, blueprint-proposals
// and arch-proposals (?status=pending|approved|rejected|all); the writes on tasks, blueprints and proposals. Required
// members are always in the server's answer.

/// <summary>GET /api/projects/{id}/blueprints: every blueprint, in the author's order.</summary>
public sealed class BlueprintList
{
    public required IReadOnlyList<BlueprintInfo> Blueprints { get; init; }
}

/// <summary>GET tasks-next-id or blueprints-next-id: the id a new one would get.</summary>
public sealed class NextId
{
    public required string Id { get; init; }
}

/// <summary>GET /api/projects/{id}/tasks/{task}/events: the task's audit trail, oldest first.</summary>
public sealed class TaskEvents
{
    public required IReadOnlyList<TaskEvent> Events { get; init; }
}

public sealed class TaskEvent
{
    public required string Event { get; init; }
    public required string Actor { get; init; }
    public required string At { get; init; }
    public string? From { get; init; }
    public string? To { get; init; }
    public string? Detail { get; init; }
}

/// <summary>
/// GET /api/projects/{id}/tasks/{task}/sessions: the agent sessions that worked on the task and what they used (the
/// hosts that steer them are the agent sessions view's).
/// </summary>
public sealed class TaskSessions
{
    public required IReadOnlyList<AgentSession> Sessions { get; init; }
    public required SessionUsage Totals { get; init; }
    public string? ClaimedAt { get; init; }
    public string? CompletedAt { get; init; }
    public string? RefreshError { get; init; }
}

/// <summary>Tokens by kind, active time and the API-equivalent price when every model used has one.</summary>
public class SessionUsage
{
    public long InputTokens { get; init; }
    public long CacheReadTokens { get; init; }
    public long CacheWriteTokens { get; init; }
    public long OutputTokens { get; init; }
    public long ReasoningTokens { get; init; }
    public long ActiveMs { get; init; }
    public double? CostUsd { get; init; }
}

/// <summary>A Claude Code or Codex session seen working on a task.</summary>
public sealed class AgentSession : SessionUsage
{
    public required string Client { get; init; }
    public required string SessionId { get; init; }

    /// <summary>harness (started from harness ui) or hooks (seen by the client hooks).</summary>
    public required string Origin { get; init; }

    /// <summary>working, blocked, released or completed: what its last call did to the task.</summary>
    public required string Status { get; init; }

    public required string StartedAt { get; init; }
    public required string EndedAt { get; init; }
    public string? Model { get; init; }
}

/// <summary>A task as an agent proposed it, or as the author restates it (dependencies as ids).</summary>
public sealed class TaskDraft
{
    public string? Id { get; init; }
    public required string Title { get; init; }
    public string? Blueprint { get; init; }
    public string? Type { get; init; }
    public string? Priority { get; init; }
    public string? Goal { get; init; }
    public IReadOnlyList<string> Scope { get; init; } = [];
    public IReadOnlyList<string> DependsOn { get; init; } = [];
    public IReadOnlyList<string> Acceptance { get; init; } = [];
}

/// <summary>What every proposal has: who proposed it and why, and how it was decided.</summary>
public abstract class Proposal
{
    public required string Id { get; init; }

    /// <summary>pending, approved or rejected.</summary>
    public required string Status { get; init; }

    public required string ProposedBy { get; init; }
    public required string ProposedAt { get; init; }
    public string? Reason { get; init; }
    public string? DecidedBy { get; init; }
    public string? DecidedAt { get; init; }
    public string? Note { get; init; }

    /// <summary>What approving it made (a task's id, a blueprint's).</summary>
    public string? Result { get; init; }
}

public sealed class TaskProposal : Proposal
{
    public required TaskDraft Task { get; init; }
}

public sealed class TaskProposalList
{
    public required IReadOnlyList<TaskProposal> Proposals { get; init; }
}

/// <summary>A blueprint's description as proposed or restated.</summary>
public sealed class BlueprintDraft
{
    public string? Id { get; init; }
    public string? Title { get; init; }
    public string? Goal { get; init; }
    public IReadOnlyList<string> Exit { get; init; } = [];
    public string? Notes { get; init; }
    public string? Color { get; init; }
}

/// <summary>A new blueprint, or a change to one (<see cref="Changes"/> names it).</summary>
public sealed class BlueprintProposal : Proposal
{
    public required BlueprintDraft Blueprint { get; init; }
    public string? Changes { get; init; }

    /// <summary>A blueprint titled the same that exists already: approving asks merge or new.</summary>
    public string? SameTitle { get; init; }
}

public sealed class BlueprintProposalList
{
    public required IReadOnlyList<BlueprintProposal> Proposals { get; init; }
}

/// <summary>An architectural task, or a by-design mark, from a harness finding or the agent's own reading.</summary>
public sealed class ArchProposal : Proposal
{
    /// <summary>task or by_design.</summary>
    public required string Kind { get; init; }

    public required string Scope { get; init; }

    /// <summary>observation (the agent's reading) or the finding's basis.</summary>
    public required string Basis { get; init; }

    public string? FindingKey { get; init; }
    public required string Title { get; init; }
    public string? Body { get; init; }
    public IReadOnlyList<string> Evidence { get; init; } = [];
}

public sealed class ArchProposalList
{
    public required IReadOnlyList<ArchProposal> Proposals { get; init; }
}

/// <summary>POST tasks (a new task) or tasks/{task}/edit (an unclaimed one restated; its id stays).</summary>
public sealed class TaskForm
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Id { get; init; }

    public required string Title { get; init; }
    public string Goal { get; init; } = "";
    public string Type { get; init; } = "feature";
    public string Priority { get; init; } = "medium";
    public string Blueprint { get; init; } = "";
    public IReadOnlyList<string> DependsOn { get; init; } = [];
    public IReadOnlyList<string> Acceptance { get; init; } = [];
    public IReadOnlyList<string> Scope { get; init; } = [];
}

/// <summary>POST blueprints (with its id) or blueprints/{id}/edit (without); no colour: harness picks one.</summary>
public sealed class BlueprintForm
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Id { get; init; }

    public required string Title { get; init; }
    public string Goal { get; init; } = "";
    public IReadOnlyList<string> Exit { get; init; } = [];
    public string Notes { get; init; } = "";

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Color { get; init; }
}

/// <summary>POST blueprints/{id}/remove: its tasks moved (to <see cref="MoveTo"/>, "" none) or deleted; none: {}.</summary>
public sealed class BlueprintRemoval
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Tasks { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? MoveTo { get; init; }
}

/// <summary>POST blueprints-order: every blueprint's id in the new order.</summary>
public sealed class BlueprintOrder
{
    public required IReadOnlyList<string> Order { get; init; }
}

/// <summary>POST tasks/{task}/blueprint: the blueprint it moves to ("" none).</summary>
public sealed class TaskMove
{
    public required string Blueprint { get; init; }
}

/// <summary>POST task-proposals/{id}/approve: the task as approved (restated or as proposed).</summary>
public sealed class TaskApproval
{
    public required TaskForm Task { get; init; }
}

/// <summary>POST blueprint-proposals/{id}/approve: the blueprint as approved; merge or new for a same title.</summary>
public sealed class BlueprintApproval
{
    public required BlueprintForm Edit { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? SameName { get; init; }
}

/// <summary>POST arch-proposals/{id}/approve: the task's title ("" for a mark) and what and why.</summary>
public sealed class ArchApproval
{
    public required string Title { get; init; }
    public required string Body { get; init; }
}

/// <summary>POST …-proposals/{id}/reject: why not (optional).</summary>
public sealed class Rejection
{
    public string Note { get; init; } = "";
}
