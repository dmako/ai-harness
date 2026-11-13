using System.Text.Json.Serialization;

namespace Harness.Ui.Api;

// The health area: GET /api/projects/{id}/health[?probe=1] (doctor's checks and the project's status) and
// POST /api/projects/{id}/fix (HarnessFix). Required members are always in the server's answer.

/// <summary>GET /api/projects/{id}/health: doctor's checks, what harness can fix, and the project's status.</summary>
public sealed class HealthReport
{
    public required HealthStatus Status { get; init; }
    public required IReadOnlyList<HealthCheck> Checks { get; init; }
    public required bool Healthy { get; init; }

    /// <summary>Whether the providers were started to check them (?probe=1).</summary>
    public required bool Probed { get; init; }

    /// <summary>The checks harness can fix itself (POST fix {check}).</summary>
    public required IReadOnlyList<string> Fixable { get; init; }

    /// <summary>The fixable checks whose fix writes a file: shown first with {check, preview: true}.</summary>
    public required IReadOnlyList<string> Previewed { get; init; }
}

/// <summary>One of doctor's checks.</summary>
public sealed class HealthCheck
{
    public required string Name { get; init; }

    /// <summary>ok, warn or fail.</summary>
    public required string Level { get; init; }

    public string? Detail { get; init; }
    public string? Fix { get; init; }
}

/// <summary>Where the project lives and the state of its database, configuration, Git and index.</summary>
public sealed class HealthStatus
{
    public required string ProjectRoot { get; init; }
    public required string StateRoot { get; init; }
    public HealthProject? Project { get; init; }
    public HealthGit? Git { get; init; }
    public string? GitError { get; init; }
    public IReadOnlyList<HealthWorktree> Worktrees { get; init; } = [];
    public required HealthDatabase Database { get; init; }
    public HealthConfig? Config { get; init; }
    public IReadOnlyList<string> Providers { get; init; } = [];
    public HealthSync? Sync { get; init; }
}

public sealed class HealthProject
{
    public required string Id { get; init; }
    public string? CopiedFrom { get; init; }
}

public sealed class HealthGit
{
    public string? Branch { get; init; }
    public string? Commit { get; init; }
    public bool Detached { get; init; }
    public bool LinkedWorktree { get; init; }
}

public sealed class HealthWorktree
{
    public required string Path { get; init; }
    public string? Branch { get; init; }
}

public sealed class HealthDatabase
{
    public required string Path { get; init; }
    public required bool Exists { get; init; }
    public int? SchemaVersion { get; init; }
    public int? SchemaLatest { get; init; }
    public string? Error { get; init; }
}

public sealed class HealthConfig
{
    public string? Global { get; init; }
    public string? Project { get; init; }
}

/// <summary>The index: when it was last synced, why it is stale, and the recent syncs (newest first).</summary>
public sealed class HealthSync
{
    public string? SyncedAt { get; init; }

    /// <summary>Why the index is stale; absent while it is fresh.</summary>
    public string? Stale { get; init; }

    public IReadOnlyList<SyncRun> Runs { get; init; } = [];
}

/// <summary>A sync: the commits it moved across, the files, and what each provider did with its index.</summary>
public sealed class SyncRun
{
    public required long Id { get; init; }
    public required string StartedAt { get; init; }
    public required string Mode { get; init; }

    /// <summary>ok, failed, partial…</summary>
    public required string Status { get; init; }

    public string? BaseCommit { get; init; }
    public string? HeadCommit { get; init; }
    public int Added { get; init; }
    public int Changed { get; init; }
    public int Removed { get; init; }
    public string? Error { get; init; }
    public string? Reason { get; init; }
    public IReadOnlyList<SyncProvider> Providers { get; init; } = [];
}

public sealed class SyncProvider
{
    public required string Name { get; init; }

    /// <summary>refreshed, failed, not_needed…</summary>
    public required string Status { get; init; }

    public string? Detail { get; init; }
}

/// <summary>POST /api/projects/{id}/fix: one check's fix (shown first with preview), or a sync.</summary>
public sealed class FixRequest
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Check { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? Preview { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? Sync { get; init; }
}

/// <summary>What a fix did: whether it worked, a line about it, and the end of its output.</summary>
public sealed class FixResult
{
    public required bool Ok { get; init; }
    public string? Message { get; init; }
    public string? Output { get; init; }
}
