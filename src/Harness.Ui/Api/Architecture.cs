using System.Text.Json.Serialization;

namespace Harness.Ui.Api;

// The architecture area (UiReadService.Evidence, Providers): GET /api/projects/{id}/structure, stack, complexity,
// coupling (from harness ui's own page: it may start providers), packages?packages=…, and POST findings/{action}.
// Required members are always in the server's answer.

/// <summary>GET …/structure: the repository's projects (or packages, folders) in layers, and its findings.</summary>
public sealed class StructureAnswer
{
    /// <summary>False when no .NET, Go or TypeScript project is found.</summary>
    public required bool Known { get; init; }

    public string? Language { get; init; }

    /// <summary>What graphify loads (.NET).</summary>
    public string? Input { get; init; }

    /// <summary>The workspaces the project is in.</summary>
    public int Workspaces { get; init; }

    public RepositoryArchitecture? Architecture { get; init; }

    /// <summary>A package's users among the workspace's members, by package name.</summary>
    public IReadOnlyDictionary<string, IReadOnlyList<PackageUser>> PackageUsers { get; init; } = new Dictionary<string, IReadOnlyList<PackageUser>>();

    /// <summary>The findings with their history and marks (after a sync); else the architecture's smells.</summary>
    public IReadOnlyList<Finding>? Findings { get; init; }

    public FindingSnapshot? Snapshot { get; init; }
}

public sealed class RepositoryArchitecture
{
    /// <summary>dotnet, go or typescript.</summary>
    public required string Language { get; init; }

    /// <summary>project, package or folder.</summary>
    public string Noun { get; init; } = "project";

    public IReadOnlyList<Solution> Solutions { get; init; } = [];
    public required IReadOnlyList<ArchProject> Projects { get; init; }
    public IReadOnlyList<Finding> Smells { get; init; } = [];

    /// <summary>How many layers: layer 0 references no other unit here.</summary>
    public int Layers { get; init; }
}

public sealed class Solution
{
    public required string Path { get; init; }
    public IReadOnlyList<string> Projects { get; init; } = [];
}

/// <summary>A unit of the repository: its layer, what it references and what references it, its packages.</summary>
public sealed class ArchProject
{
    public required string Name { get; init; }
    public required string Path { get; init; }
    public int Layer { get; init; }

    /// <summary>library, entry or test.</summary>
    public string? Kind { get; init; }

    /// <summary>model, data, logic, api, test, base, hooks, ui, feature, page: read from the name.</summary>
    public string? Role { get; init; }

    /// <summary>The package it publishes.</summary>
    public string? Package { get; init; }

    public IReadOnlyList<string> References { get; init; } = [];
    public IReadOnlyList<string> ReferencedBy { get; init; } = [];

    /// <summary>Built first but not referenced as code (ReferenceOutputAssembly=false).</summary>
    public IReadOnlyList<string> BuildOrder { get; init; } = [];

    public IReadOnlyList<string> BuildOrderBy { get; init; } = [];
    public IReadOnlyList<PackageReference> Packages { get; init; } = [];
    public IReadOnlyList<string> Solutions { get; init; } = [];
}

public sealed class PackageReference
{
    public required string Name { get; init; }
    public string? Version { get; init; }
    public bool Unresolved { get; init; }
}

public sealed class PackageUser
{
    public required string MemberId { get; init; }
    public required string Member { get; init; }
    public required string Project { get; init; }
}

/// <summary>
/// A finding: what it is, how severe, why, what could help, the places that show it, who found it, and its history
/// (new, changed, gone) and mark (by design, task, back for review).
/// </summary>
public sealed class Finding
{
    public required string Kind { get; init; }

    /// <summary>problem, warn or info.</summary>
    public required string Level { get; init; }

    public required string Title { get; init; }
    public string? Why { get; init; }
    public string? Fix { get; init; }
    public string? Project { get; init; }
    public IReadOnlyList<string> Evidence { get; init; } = [];
    public string? Key { get; init; }
    public FindingSource? Source { get; init; }

    /// <summary>fact or heuristic.</summary>
    public string? Basis { get; init; }

    public double? Confidence { get; init; }

    /// <summary>new, changed, same or gone since the last change of the findings.</summary>
    public string? Status { get; init; }

    public FindingMark? Mark { get; init; }

    /// <summary>The recorded rule a boundary finding breaks, or the knowledge a stale finding names.</summary>
    public FindingRule? Rule { get; init; }
}

public sealed class FindingSource
{
    public string? Provider { get; init; }
    public string? Tool { get; init; }
}

public sealed class FindingMark
{
    /// <summary>by_design, task or review.</summary>
    public required string State { get; init; }

    public string? Reason { get; init; }
    public string? TaskId { get; init; }
    public string? TaskProject { get; init; }
    public string? ReviewReason { get; init; }
    public string? ProposedBy { get; init; }
}

public sealed class FindingRule
{
    public string? Line { get; init; }
    public string? Type { get; init; }
    public string? Scope { get; init; }
    public string? Memory { get; init; }
}

/// <summary>What changed in the findings at the last sync.</summary>
public sealed class FindingSnapshot
{
    public string? TakenAt { get; init; }
    public int Added { get; init; }
    public int Changed { get; init; }
    public int Gone { get; init; }
}

/// <summary>POST findings/{mark|task|unmark}: the finding's key, and the reason (mark) or member (task).</summary>
public sealed class FindingAction
{
    public required string Key { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Reason { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Project { get; init; }
}

/// <summary>GET …/stack: what the project is built on, and the workspace packages it uses.</summary>
public sealed class StackAnswer
{
    public ProjectStack? Stack { get; init; }
    public IReadOnlyList<StackUse> Uses { get; init; } = [];
}

public sealed class ProjectStack
{
    public required string Language { get; init; }
    public StackPart? Runtime { get; init; }
    public IReadOnlyList<StackPart> Frameworks { get; init; } = [];
    public string? Badge { get; init; }
}

public sealed class StackPart
{
    public required string Name { get; init; }
    public string? Version { get; init; }

    /// <summary>Where it is declared.</summary>
    public string? At { get; init; }
}

/// <summary>A workspace member's package this project uses: the versions used and the one declared there.</summary>
public sealed class StackUse
{
    public required string Package { get; init; }
    public required string MemberId { get; init; }
    public required string Member { get; init; }
    public IReadOnlyList<string> Versions { get; init; } = [];
    public string? Declared { get; init; }
}

/// <summary>GET …/complexity: every function by cyclomatic complexity, per language reported.</summary>
public sealed class ComplexityAnswer
{
    public required IReadOnlyList<LanguageComplexity> Languages { get; init; }
    public int BinWidth { get; init; } = 5;
}

public sealed class LanguageComplexity
{
    public required string Language { get; init; }
    public required ComplexityThresholds Thresholds { get; init; }
    public int Functions { get; init; }
    public int Max { get; init; }
    public required IReadOnlyList<ComplexityBin> Bins { get; init; }
    public IReadOnlyList<ComplexFunction> Top { get; init; } = [];

    /// <summary>Each file's most complex function.</summary>
    public IReadOnlyDictionary<string, int> Files { get; init; } = new Dictionary<string, int>();
}

public sealed class ComplexityThresholds
{
    public int Note { get; init; } = 11;
    public int Warn { get; init; } = 21;
    public int Problem { get; init; } = 51;
}

public sealed class ComplexityBin
{
    public required int From { get; init; }
    public required int To { get; init; }
    public required int Count { get; init; }
}

public sealed class ComplexFunction
{
    public required string Name { get; init; }
    public required string Path { get; init; }
    public int Line { get; init; }
    public required int Complexity { get; init; }
}

/// <summary>GET …/coupling: the files changed most over 180 days, what changes with them, and partners left out.</summary>
public sealed class CouplingAnswer
{
    public IReadOnlyList<Hotspot> Hotspots { get; init; } = [];
    public int Commits { get; init; }
    public int WindowDays { get; init; }
    public IReadOnlyList<string> Errors { get; init; } = [];

    /// <summary>The uncommitted change's files; null without one.</summary>
    public IReadOnlyList<string>? Changed { get; init; }

    public IReadOnlyList<MissingPartner> Missing { get; init; } = [];
}

public sealed class Hotspot
{
    public required string Path { get; init; }
    public required int Commits { get; init; }
    public IReadOnlyList<Partner> Partners { get; init; } = [];
}

/// <summary>A file that changes with another: how often together, and the share of the other's commits.</summary>
public sealed class Partner
{
    public required string Path { get; init; }
    public int Together { get; init; }
    public double Share { get; init; }
    public string? Provider { get; init; }
    public string? Tool { get; init; }
}

public sealed class MissingPartner
{
    public required string Changed { get; init; }
    public required Partner Partner { get; init; }
}

/// <summary>GET …/packages?packages=name@version,…: newer stable versions and known vulnerabilities.</summary>
public sealed class PackageMarks
{
    public IReadOnlyDictionary<string, PackageMark> Packages { get; init; } = new Dictionary<string, PackageMark>();
    public IReadOnlyList<string> Notes { get; init; } = [];
}

public sealed class PackageMark
{
    public string? Version { get; init; }

    /// <summary>A newer stable version, when there is one.</summary>
    public string? Latest { get; init; }

    public IReadOnlyList<Vulnerability> Vulnerabilities { get; init; } = [];
}

public sealed class Vulnerability
{
    public required string Severity { get; init; }

    [JsonPropertyName("url")]
    public string? Advisory { get; init; }

    public string? Versions { get; init; }
    public string? Fixed { get; init; }
}
