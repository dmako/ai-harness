using System.Text.Json.Serialization;

namespace Harness.Ui.Api;

// The agents area (UiReadService.Providers, ProjectScm): GET /api/projects/{id}/providers?days=N, scm?days=N and
// hooks, and the writes scm/choice and scm/test. Required members are always in the server's answer.

/// <summary>GET …/providers: the code providers harness routes questions to, and what architecture evidence lacks one.</summary>
public sealed class ProvidersAnswer
{
    public IReadOnlyList<string> Languages { get; init; } = [];

    /// <summary>Null when none is configured.</summary>
    public IReadOnlyList<ProviderInfo>? Providers { get; init; }

    public IReadOnlyList<ArchitectureCoverage> Architecture { get; init; } = [];
}

public sealed class ProviderInfo
{
    public required string Name { get; init; }

    /// <summary>harness (provider protocol) or mcp.</summary>
    public required string Type { get; init; }

    public string? Command { get; init; }
    public required bool Enabled { get; init; }
    public string? Pack { get; init; }

    /// <summary>Why it is not used here (disabled, or serving other languages); absent when it is.</summary>
    public string? Inactive { get; init; }

    public IReadOnlyList<string> Languages { get; init; } = [];
    public IReadOnlyList<string> Only { get; init; } = [];
    public IReadOnlyList<ProviderCapability>? Capabilities { get; init; }

    /// <summary>A check of its configuration (the nuget provider's), with a fix harness can run.</summary>
    public ProviderCheck? Check { get; init; }
}

public sealed class ProviderCapability
{
    public required string Capability { get; init; }
    public bool Declared { get; init; }
    public string? Tool { get; init; }
    public CapabilityMetrics? Metrics { get; init; }
    public CallTrend? Trend { get; init; }
}

public sealed class CapabilityMetrics
{
    public int Calls { get; init; }
    public int Errors { get; init; }
    public double ErrorRate { get; init; }
}

public sealed class ProviderCheck
{
    public required string Name { get; init; }
    public required string Level { get; init; }
    public string? Detail { get; init; }
    public string? Fix { get; init; }
    public bool Fixable { get; init; }
}

/// <summary>An architecture capability, the providers serving it, and what is missing without one.</summary>
public sealed class ArchitectureCoverage
{
    public required string Capability { get; init; }
    public IReadOnlyList<string> Providers { get; init; } = [];
    public string? Without { get; init; }
}

/// <summary>GET …/scm: the project's SCM, how it was chosen, the connections serving its remote, the last test, use.</summary>
public sealed class ProjectScm
{
    /// <summary>Whether any SCM connection exists on this machine.</summary>
    public required bool Configured { get; init; }

    public bool Repository { get; init; }
    public ScmResolution? Scm { get; init; }
    public string? Chosen { get; init; }

    /// <summary>The connection chosen on this machine for this repository; null is automatic.</summary>
    public string? Choice { get; init; }

    /// <summary>[scm] name in the project's shared configuration: it decides, the choice cannot.</summary>
    public string? ConfigName { get; init; }

    public IReadOnlyList<ScmCandidate> Candidates { get; init; } = [];
    public ProjectScmTest? LastTest { get; init; }
    public ScmStats? Stats { get; init; }
}

/// <summary>How the project's SCM resolved: matched (name, type, URL, project, remote) or why not.</summary>
public sealed class ScmResolution
{
    public required string Status { get; init; }
    public string? Source { get; init; }
    public string? Reason { get; init; }
    public string? Name { get; init; }
    public string? Type { get; init; }

    [JsonPropertyName("url")]
    public string? Address { get; init; }

    public string? Project { get; init; }
    public string? Remote { get; init; }
}

public sealed class ScmCandidate
{
    public required string Name { get; init; }
    public string? Type { get; init; }

    [JsonPropertyName("url")]
    public string? Address { get; init; }

    public string? Token { get; init; }

    /// <summary>A stale choice no longer configured.</summary>
    public bool Missing { get; init; }
}

/// <summary>A test of the project's SCM: the connection's, and whether its project is reachable with that token.</summary>
public sealed class ProjectScmTest
{
    public required ScmTest Test { get; init; }
    public required ScmProjectCheck Project { get; init; }
}

public sealed class ScmProjectCheck
{
    public required string Level { get; init; }
    public string? Detail { get; init; }
    public string? Fix { get; init; }
}

public sealed class ProjectScmTestAnswer
{
    public required ProjectScmTest Test { get; init; }
}

/// <summary>How agents called the scm_ tools over the window.</summary>
public sealed class ScmStats
{
    public required int Days { get; init; }
    public IReadOnlyList<ToolUse> Tools { get; init; } = [];
    public CallTrend? Trend { get; init; }
}

/// <summary>POST …/scm/choice: the connection to use on this machine; "" detects it from the remotes again.</summary>
public sealed class ScmChoice
{
    public required string Name { get; init; }
}

/// <summary>GET …/hooks: the Git sync hooks and the client hooks as their files say, and a week's events.</summary>
public sealed class HooksAnswer
{
    public IReadOnlyList<GitHook> Git { get; init; } = [];
    public string? GitError { get; init; }
    public string? GitDir { get; init; }
    public IReadOnlyList<ClientHook> Clients { get; init; } = [];
    public HookSummary? Events { get; init; }
    public string? Note { get; init; }
}

public sealed class GitHook
{
    public required string Name { get; init; }
    public bool Installed { get; init; }
}

public sealed class ClientHook
{
    public required string Client { get; init; }
    public required string Scope { get; init; }
    public required string Path { get; init; }

    /// <summary>The tool-event hook's command, when installed.</summary>
    public string? Command { get; init; }

    public string? Problem { get; init; }

    /// <summary>Claude Code's session-start hook's command, when installed.</summary>
    public string? Session { get; init; }

    public string? Error { get; init; }
}
