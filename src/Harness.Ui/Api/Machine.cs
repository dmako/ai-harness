using System.Text.Json.Serialization;

namespace Harness.Ui.Api;

// The machine area (UiReadService.Machine, Scm, Writes): GET /api/machine, /api/board-defaults, /api/scm,
// /api/graphify/sessions[?project=…], /api/search?q=…, /api/docs/{slug}, and the writes on board defaults, telemetry,
// SCM connections and graphify sessions. Required members are always in the server's answer.

/// <summary>GET /api/machine: this installation, where its state lives, and the telemetry opt-in.</summary>
public sealed class MachineInfo
{
    public required BuildInfo Build { get; init; }
    public required MachinePaths Paths { get; init; }
    public required TelemetryState Telemetry { get; init; }
}

public sealed class BuildInfo
{
    public required string Version { get; init; }
    public string? Commit { get; init; }
    public string? Date { get; init; }
    public string? Runtime { get; init; }
    public string? Platform { get; init; }
}

public sealed class MachinePaths
{
    public required string Global { get; init; }
    public string? Registry { get; init; }
    public string? Workspaces { get; init; }
    public string? Knowledge { get; init; }
}

/// <summary>The developer telemetry opt-in: on or off, where to, and when it last sent (its token never comes back).</summary>
public sealed class TelemetryState
{
    public required bool Enabled { get; init; }
    public string? Endpoint { get; init; }
    public string? LastSent { get; init; }
}

/// <summary>POST /api/telemetry: on with the collector's endpoint (and token), or off.</summary>
public sealed class TelemetryChange
{
    public required bool Enabled { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Endpoint { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Token { get; init; }
}

public sealed class TelemetryAnswer
{
    public required TelemetryState Telemetry { get; init; }
}

/// <summary>GET and POST /api/board-defaults: how a project's Board starts until it is set there.</summary>
public sealed class BoardDefaultsAnswer
{
    public required BoardDefaults Defaults { get; init; }
}

public sealed class BoardDefaults
{
    public required string Sort { get; init; }
    public required int HideDoneDays { get; init; }
}

/// <summary>POST /api/board-defaults: what changes.</summary>
public sealed class BoardDefaultsChange
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Sort { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? HideDoneDays { get; init; }
}

/// <summary>GET /api/scm: this machine's SCM connections (scm.toml), tokens only masked.</summary>
public sealed class ScmList
{
    public string? File { get; init; }
    public IReadOnlyList<string> Types { get; init; } = [];
    public required IReadOnlyList<ScmEntry> Scm { get; init; }
}

public sealed class ScmEntry
{
    public required string Name { get; init; }

    /// <summary>gitlab, forgejo or github.</summary>
    public required string Type { get; init; }

    /// <summary>The server's base address as typed (it may be one the server refuses).</summary>
    [JsonPropertyName("url")]
    public required string Address { get; init; }

    public IReadOnlyList<string> Aliases { get; init; } = [];

    /// <summary>stored or env.</summary>
    public string? TokenSource { get; init; }

    public string? TokenEnv { get; init; }

    /// <summary>The token masked (****abcd), never in full.</summary>
    public string? Token { get; init; }

    public bool TokenAvailable { get; init; }
    public bool? TokenEncrypted { get; init; }
    public string? TokenError { get; init; }
    public ScmTest? LastTest { get; init; }
}

/// <summary>A connection test: connected (version, user), or the failure's kind, message and what to do.</summary>
public sealed class ScmTest
{
    public required bool Ok { get; init; }
    public string? Version { get; init; }
    public string? User { get; init; }
    public string? Failure { get; init; }
    public string? Message { get; init; }
    public string? Hint { get; init; }
    public string? At { get; init; }
}

public sealed class ScmTestAnswer
{
    public required ScmTest Test { get; init; }
}

/// <summary>POST /api/scm (add) or /api/scm/{name}/edit (an empty token keeps the stored one), or /api/scm/test.</summary>
public sealed class ScmForm
{
    public required string Name { get; init; }
    public required string Type { get; init; }

    [JsonPropertyName("url")]
    public required string Address { get; init; }

    public string Token { get; init; } = "";

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<string>? Aliases { get; init; }
}

/// <summary>GET /api/graphify/sessions: graphify-csharp's warm sessions, the machine's or one project's.</summary>
public sealed class GraphifySessions
{
    public required IReadOnlyList<GraphifySession> Sessions { get; init; }
    public string? Error { get; init; }
}

public sealed class GraphifySession
{
    public required string Id { get; init; }
    public required string Root { get; init; }
    public string? State { get; init; }
    public bool Ready { get; init; }

    /// <summary>"ready, 0.1 GB, up 2h, started by harness": the state first, then what it says.</summary>
    public string? Summary { get; init; }

    public string? ProjectId { get; init; }
    public string? ProjectName { get; init; }
}

/// <summary>POST /api/graphify/sessions/{start|restart|stop}: the project (start, restart) or the session (stop).</summary>
public sealed class GraphifyAction
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Project { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Session { get; init; }
}

/// <summary>GET /api/search?q=…: projects, workspaces, memories, tasks and blueprints, up to per_kind each.</summary>
public sealed class SearchAnswer
{
    public required string Query { get; init; }
    public required IReadOnlyList<SearchHit> Hits { get; init; }
    public required int PerKind { get; init; }
}

public sealed class SearchHit
{
    /// <summary>project, workspace, memory, task or blueprint.</summary>
    public required string Kind { get; init; }

    public required string Title { get; init; }

    /// <summary>A project's folder, a memory's type, a task's state…</summary>
    public string? Detail { get; init; }

    public string? ProjectId { get; init; }
    public string? Project { get; init; }
    public string? Workspace { get; init; }
    public string? Ref { get; init; }
}

/// <summary>GET /api/docs/{slug}: one section of the guide or reference, as Markdown.</summary>
public sealed class DocText
{
    public required string Doc { get; init; }
    public required string Slug { get; init; }
    public required string Title { get; init; }
    public required string Body { get; init; }
}
