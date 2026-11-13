using System.Text.Json.Serialization;

namespace Harness.Ui.Api;

// The tasks and Board area: GET/POST /api/projects/{id}/board (UiReadService.Board, TaskViews.Task/Blueprint).
// Required members are always in the server's answer.

/// <summary>GET /api/projects/{id}/board: one column per status, sorted and filtered by the server.</summary>
public sealed class BoardResponse
{
    public required BoardSettings Settings { get; init; }
    public required BoardSettings Defaults { get; init; }
    public required IReadOnlyList<string> Sorts { get; init; }
    public required IReadOnlyList<int> HideDoneDaysOptions { get; init; }
    public required IReadOnlyList<BoardColumn> Columns { get; init; }
    public required IReadOnlyList<BlueprintInfo> Blueprints { get; init; }
}

/// <summary>A Board's choices: sort, done-age and blueprint filter ("" all, "—" none; defaults have none).</summary>
public sealed class BoardSettings
{
    public required string Sort { get; init; }
    public required int HideDoneDays { get; init; }
    public string? Blueprint { get; init; }
}

/// <summary>POST /api/projects/{id}/board: what changes; null fields stay.</summary>
public sealed class BoardChange
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Sort { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? HideDoneDays { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Blueprint { get; init; }
}

public sealed class BoardColumn
{
    public required string Status { get; init; }
    public required IReadOnlyList<TaskSummary> Tasks { get; init; }
}

/// <summary>A task as the Board and lists show it (TaskViews.Task).</summary>
public sealed class TaskSummary
{
    public required string Id { get; init; }
    public required string Title { get; init; }
    public required string Status { get; init; }
    public string Priority { get; init; } = "medium";
    public string Type { get; init; } = "feature";
    public string? Blueprint { get; init; }
    public string? Goal { get; init; }
    public string? BlockedReason { get; init; }
    public TaskCompletion? Completion { get; init; }
}

/// <summary>How a done task was completed.</summary>
public sealed class TaskCompletion
{
    public string? Summary { get; init; }
    public string? CompletedBy { get; init; }
    public string? CompletedAt { get; init; }
}

/// <summary>A blueprint as the Board names it (TaskViews.Blueprint).</summary>
public sealed class BlueprintInfo
{
    public required string Id { get; init; }
    public string? Title { get; init; }
    public string? Color { get; init; }
}
