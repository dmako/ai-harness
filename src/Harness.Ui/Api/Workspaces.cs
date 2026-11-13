using System.Text.Json.Serialization;

namespace Harness.Ui.Api;

// The workspaces area: GET /api/workspaces (WorkspaceStore). Required members are always in the server's answer.

/// <summary>GET /api/workspaces.</summary>
public sealed class WorkspaceList
{
    public required IReadOnlyList<WorkspaceInfo> Workspaces { get; init; }
}

/// <summary>A workspace: projects searched and reasoned about together.</summary>
public sealed class WorkspaceInfo
{
    public required string Id { get; init; }

    /// <summary>The short name (workspace:&lt;name&gt; for agents and commands).</summary>
    public required string Name { get; init; }

    public string? Title { get; init; }
    public string? Created { get; init; }
    public required IReadOnlyList<WorkspaceMember> Members { get; init; }

    [JsonIgnore]
    public string DisplayName => Title is { Length: > 0 } title ? title : Name;
}

public sealed class WorkspaceMember
{
    public required string ProjectId { get; init; }
    public string? Project { get; init; }
    public string? Root { get; init; }
    public string? Title { get; init; }
    public string? State { get; init; }
}
