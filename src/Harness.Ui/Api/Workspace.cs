using System.Text.Json.Serialization;

namespace Harness.Ui.Api;

// The workspace area (UiReadService.Evidence, Reviews): GET /api/workspaces/{name}/system, dependencies, stack,
// findings, issues, duplicates, adoption?library=…, GET /api/workspaces/discover?folder=…, and POST /api/workspaces,
// {name}/join, leave, delete and findings/{action}. Required members are always in the server's answer.

/// <summary>GET …/system: the workspace as a system, its services, outside systems and databases, and their calls.</summary>
public sealed class SystemAnswer
{
    public required IReadOnlyList<SystemNode> Nodes { get; init; }
    public required IReadOnlyList<SystemLink> Links { get; init; }
    public IReadOnlyList<SystemContract> Contracts { get; init; } = [];

    /// <summary>The system's smells as read now.</summary>
    public IReadOnlyList<Finding> Smells { get; init; } = [];

    /// <summary>The smells with their history and marks (after a sync); empty before.</summary>
    public IReadOnlyList<Finding>? Findings { get; init; }

    public FindingSnapshot? Snapshot { get; init; }
    public IReadOnlyList<string>? Notes { get; init; }
}

public sealed class SystemNode
{
    public required string Id { get; init; }
    public required string Name { get; init; }

    /// <summary>service, frontend, library, external, broker or database.</summary>
    public required string Kind { get; init; }

    public string? MemberId { get; init; }

    /// <summary>For a database "per tenant"; for an outside system who declares it.</summary>
    public string? Detail { get; init; }

    /// <summary>Callers above the called: the higher, the more it calls.</summary>
    public int Layer { get; init; }
}

public sealed class SystemLink
{
    public required string From { get; init; }
    public required string To { get; init; }

    /// <summary>grpc, http, messaging or database.</summary>
    public required string Style { get; init; }

    public IReadOnlyList<string> What { get; init; } = [];
    public IReadOnlyList<string>? Contracts { get; init; }
    public int Uses { get; init; }
    public IReadOnlyList<string> Evidence { get; init; } = [];
    public string? Basis { get; init; }
}

public sealed class SystemContract
{
    public required string Name { get; init; }
    public required string Owner { get; init; }
    public string? Declared { get; init; }
    public IReadOnlyList<string> Servers { get; init; } = [];
    public IReadOnlyList<string> Clients { get; init; } = [];
}

/// <summary>GET …/dependencies: which members compile against another member's packages.</summary>
public sealed class DependencyAnswer
{
    public IReadOnlyList<DependencyNode> Nodes { get; init; } = [];
    public IReadOnlyList<DependencyEdge> Edges { get; init; } = [];
}

public sealed class DependencyNode
{
    public required string Project { get; init; }
    public required string ProjectId { get; init; }
    public IReadOnlyList<string> Packages { get; init; } = [];

    /// <summary>No host or app of its own.</summary>
    public bool Library { get; init; }
}

public sealed class DependencyEdge
{
    public required string From { get; init; }
    public required string To { get; init; }
    public IReadOnlyList<string> Packages { get; init; } = [];
    public int Refs { get; init; }
    public IReadOnlyList<ManifestReference> Via { get; init; } = [];
}

public sealed class ManifestReference
{
    public required string Path { get; init; }
    public int Line { get; init; }
    public string? Matched { get; init; }
}

/// <summary>GET …/stack: each member's stack, and the versions of what they share, compared.</summary>
public sealed class WorkspaceStackAnswer
{
    public IReadOnlyDictionary<string, ProjectStack> Stacks { get; init; } = new Dictionary<string, ProjectStack>();
    public IReadOnlyList<StackVersionRow> Rows { get; init; } = [];
}

public sealed class StackVersionRow
{
    /// <summary>runtime, framework or core (a member's own package).</summary>
    public required string Kind { get; init; }

    public required string Name { get; init; }

    /// <summary>The versions each member uses, by member id.</summary>
    public IReadOnlyDictionary<string, IReadOnlyList<string>> Versions { get; init; } = new Dictionary<string, IReadOnlyList<string>>();

    /// <summary>The member whose package it is.</summary>
    public string? Owner { get; init; }

    /// <summary>The version its owner declares now.</summary>
    public string? Published { get; init; }

    public bool Differ { get; init; }
}

/// <summary>GET …/findings: the open findings per member and between them, as each one's last sync recorded them.</summary>
public sealed class RollupAnswer
{
    public IReadOnlyList<RollupRow> Rows { get; init; } = [];
}

public sealed class RollupRow
{
    /// <summary>member, system or shared.</summary>
    public required string Kind { get; init; }

    public required string Name { get; init; }
    public string? MemberId { get; init; }

    /// <summary>The open findings by level (problem, warn, info).</summary>
    public IReadOnlyDictionary<string, int> Levels { get; init; } = new Dictionary<string, int>();

    public int ByDesign { get; init; }
    public int Review { get; init; }

    /// <summary>Why there are no counts: not synced, not readable.</summary>
    public string? Note { get; init; }

    public FindingSnapshot? Snapshot { get; init; }
}

/// <summary>GET …/issues: design smells only seen across repositories.</summary>
public sealed class IssuesAnswer
{
    public IReadOnlyList<DesignIssue> Issues { get; init; } = [];
}

public sealed class DesignIssue
{
    /// <summary>cycle, local-copy, duplicate, version-drift or hub.</summary>
    public required string Kind { get; init; }

    public required string Level { get; init; }
    public string? Subject { get; init; }
    public string? Summary { get; init; }
    public required string Title { get; init; }
    public string? Why { get; init; }
    public IReadOnlyList<string> Projects { get; init; } = [];
    public IReadOnlyList<IssueEvidence> Evidence { get; init; } = [];
}

public sealed class IssueEvidence
{
    public required string Project { get; init; }
    public string? Path { get; init; }
    public int Line { get; init; }
    public string? Note { get; init; }
}

/// <summary>GET …/duplicates: code in several members by name, and the copies worth acting on as findings.</summary>
public sealed class DuplicatesAnswer
{
    public IReadOnlyList<DuplicateGroup> Groups { get; init; } = [];
    public IReadOnlyList<string>? Notes { get; init; }
    public IReadOnlyList<Finding>? Findings { get; init; }
    public FindingSnapshot? Snapshot { get; init; }
}

public sealed class DuplicateGroup
{
    public required string Name { get; init; }
    public string? Kind { get; init; }
    public required IReadOnlyList<DuplicateCopy> Copies { get; init; }
    public double Similarity { get; init; }

    /// <summary>identical, near copy, similar, same name only or extends.</summary>
    public required string Verdict { get; init; }

    public IReadOnlyList<string>? Names { get; init; }
    public SharedCodeHome? Home { get; init; }
}

public sealed class DuplicateCopy
{
    public required string Project { get; init; }
    public required string Path { get; init; }
    public int Lines { get; init; }
    public int Line { get; init; }
    public bool Extends { get; init; }
}

/// <summary>Where repeated code could live once.</summary>
public sealed class SharedCodeHome
{
    public required string Kind { get; init; }
    public required string Reason { get; init; }
    public string? MemberId { get; init; }
    public string? Member { get; init; }
    public IReadOnlyList<string>? Knowledge { get; init; }
}

/// <summary>GET …/adoption?library=…: which of a member's packages are used, and by whom.</summary>
public sealed class LibraryAdoption
{
    public required string Library { get; init; }
    public IReadOnlyList<string> Packages { get; init; } = [];
    public IReadOnlyList<string>? Namespaces { get; init; }
    public int PublicTypes { get; init; }
    public IReadOnlyList<LibraryConsumer> Consumers { get; init; } = [];
    public IReadOnlyList<string> NonConsumers { get; init; } = [];
    public IReadOnlyList<LocalImplementation> PossibleLocalImplementations { get; init; } = [];
    public IReadOnlyList<string>? Notes { get; init; }
}

public sealed class LibraryConsumer
{
    public required string Project { get; init; }
    public IReadOnlyList<ManifestReference>? Packages { get; init; }
    public IReadOnlyList<string>? Namespaces { get; init; }
    public int ImportFiles { get; init; }
    public IReadOnlyList<ManifestReference>? Imports { get; init; }
}

/// <summary>A consumer's own type with the name of one the library declares.</summary>
public sealed class LocalImplementation
{
    public required string Project { get; init; }
    public required DeclaredType Declared { get; init; }
    public required DeclaredType Library { get; init; }
}

public sealed class DeclaredType
{
    public required string Name { get; init; }
    public string? Kind { get; init; }
    public string? Path { get; init; }
    public int Line { get; init; }
}

/// <summary>GET /api/workspaces/discover?folder=…: the registered projects under a folder.</summary>
public sealed class DiscoverAnswer
{
    public required string Folder { get; init; }
    public IReadOnlyList<DiscoveredProject> Projects { get; init; } = [];
}

public sealed class DiscoveredProject
{
    public required string ProjectId { get; init; }
    public string? Project { get; init; }
    public string? Root { get; init; }
}

/// <summary>POST /api/workspaces: a new workspace, its title and its first members.</summary>
public sealed class WorkspaceCreate
{
    public required string Name { get; init; }
    public string Title { get; init; } = "";
    public IReadOnlyList<string> ProjectIds { get; init; } = [];
}

/// <summary>POST …/join: the projects to add.</summary>
public sealed class WorkspaceJoin
{
    public required IReadOnlyList<string> ProjectIds { get; init; }
}

/// <summary>POST …/leave: the project to take out.</summary>
public sealed class WorkspaceLeave
{
    public required string ProjectId { get; init; }
}

/// <summary>What a workspace write answers: the workspace and the projects added.</summary>
public sealed class WorkspaceWritten
{
    public string? Workspace { get; init; }
    public IReadOnlyList<string>? Added { get; init; }
    public string? Removed { get; init; }
    public string? Deleted { get; init; }
}
