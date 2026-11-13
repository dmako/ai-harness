using System.Text.Json.Serialization;

namespace Harness.Ui.Api;

// Typed views of the harness ui API's answers, only the fields this UI reads. The server builds its answers as
// JSON object maps (Harness.Cli UiReadService), so these are the first typed contract of that API; unknown fields
// are ignored.

/// <summary>GET /api/projects.</summary>
public sealed class ProjectList
{
    public IReadOnlyList<ProjectInfo> Projects { get; init; } = [];
}

/// <summary>A registered project as harness ui lists it.</summary>
public sealed class ProjectInfo
{
    public string Id { get; init; } = "";

    /// <summary>The display name chosen with rename, if any.</summary>
    public string? Title { get; init; }

    /// <summary>The short name chosen with rename (commands and agents use it), if any.</summary>
    public string? Name { get; init; }

    public string Root { get; init; } = "";
    public ProjectLive Live { get; init; } = new();

    /// <summary>The short names of the workspaces it belongs to.</summary>
    public IReadOnlyList<string> Workspaces { get; init; } = [];

    public string? LastMcp { get; init; }
    public IReadOnlyList<string> Clients { get; init; } = [];

    /// <summary>The root folder's name.</summary>
    [JsonIgnore]
    public string Folder
    {
        get
        {
            var folder = Root.TrimEnd('\\', '/');
            return folder[(folder.LastIndexOfAny(['\\', '/']) + 1)..];
        }
    }

    /// <summary>What the page calls it (app.js pname): the title, the short name, else the folder's name.</summary>
    [JsonIgnore]
    public string DisplayName
    {
        get
        {
            if (Title is { Length: > 0 } title)
            {
                return title;
            }
            if (Name is { Length: > 0 } name)
            {
                return name;
            }
            return Folder.Length > 0 ? Folder : Id;
        }
    }

    /// <summary>How many things wait for a person here (app.js waitingIn): proposals, failing checks, unreadable state.</summary>
    [JsonIgnore]
    public int Waiting => Live.KnowledgeWaiting + Live.WorkProposals + Live.ChecksFailed + (Live.State is "ok" ? 0 : 1);
}

/// <summary>A project's live state (UiProjectCatalog): readable or not, what waits, and where its code is.</summary>
public sealed class ProjectLive
{
    /// <summary>ok, missing, error, migrate_needed, newer_schema…</summary>
    public string State { get; init; } = "ok";

    public bool Stale { get; init; }
    public string? Detail { get; init; }
    public int PendingProposals { get; init; }
    public int PendingArchProposals { get; init; }
    public int PendingTaskProposals { get; init; }
    public int PendingBlueprintProposals { get; init; }
    public int RemovedFromProject { get; init; }
    public int ChecksFailed { get; init; }
    public int ChecksWarned { get; init; }
    public string? Branch { get; init; }
    public string? Commit { get; init; }
    public string? SyncedAt { get; init; }

    /// <summary>The Knowledge Inbox (app.js knowledgeWaiting): proposals, and memories a Git change took out.</summary>
    [JsonIgnore]
    public int KnowledgeWaiting => PendingProposals + RemovedFromProject;

    /// <summary>The Work Inbox (app.js workProposals).</summary>
    [JsonIgnore]
    public int WorkProposals => PendingArchProposals + PendingTaskProposals + PendingBlueprintProposals;

    /// <summary>ok, stale, warn or bad (app.js stateKind): the shape the sidebar draws.</summary>
    [JsonIgnore]
    public string Kind =>
        State switch
        {
            "ok" => Stale ? "stale" : "ok",
            "missing" or "error" => "bad",
            _ => "warn",
        };

    /// <summary>The words for the state (app.js STATE_WORD, or the state itself when not ok).</summary>
    [JsonIgnore]
    public string Words =>
        Kind switch
        {
            "ok" => "ok",
            "stale" => "stale index",
            _ => State.Replace('_', ' '),
        };
}

/// <summary>GET /api/workspaces.</summary>
public sealed class WorkspaceList
{
    public IReadOnlyList<WorkspaceInfo> Workspaces { get; init; } = [];
}

/// <summary>A workspace: projects searched and reasoned about together.</summary>
public sealed class WorkspaceInfo
{
    public string Id { get; init; } = "";

    /// <summary>The short name (workspace:&lt;name&gt; for agents and commands).</summary>
    public string Name { get; init; } = "";

    public string? Title { get; init; }
    public string? Created { get; init; }
    public IReadOnlyList<WorkspaceMember> Members { get; init; } = [];

    [JsonIgnore]
    public string DisplayName => Title is { Length: > 0 } title ? title : Name;
}

public sealed class WorkspaceMember
{
    public string ProjectId { get; init; } = "";
    public string? Project { get; init; }
    public string? Root { get; init; }
    public string? Title { get; init; }
    public string? State { get; init; }
}

/// <summary>POST /api/projects/{id}/rename and /api/workspaces/{name}/rename: what changes; null stays.</summary>
public sealed class RenameChange
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Title { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Name { get; init; }
}

/// <summary>GET /api/projects/{id}/board: one column per status, sorted and filtered by the server.</summary>
public sealed class BoardResponse
{
    public BoardSettings Settings { get; init; } = new();
    public BoardSettings Defaults { get; init; } = new();
    public IReadOnlyList<string> Sorts { get; init; } = [];
    public IReadOnlyList<int> HideDoneDaysOptions { get; init; } = [];
    public IReadOnlyList<BoardColumn> Columns { get; init; } = [];
    public IReadOnlyList<BlueprintInfo> Blueprints { get; init; } = [];
}

/// <summary>A Board's choices: sort, done-age and blueprint filter ("" all, "—" none).</summary>
public sealed class BoardSettings
{
    public string Sort { get; init; } = "priority";
    public int HideDoneDays { get; init; } = 7;
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
    public string Status { get; init; } = "";
    public IReadOnlyList<TaskSummary> Tasks { get; init; } = [];
}

public sealed class TaskSummary
{
    public string Id { get; init; } = "";
    public string Title { get; init; } = "";
    public string Status { get; init; } = "";
    public string Priority { get; init; } = "medium";
    public string Type { get; init; } = "feature";
    public string? Blueprint { get; init; }
    public string? Goal { get; init; }
}

public sealed class BlueprintInfo
{
    public string Id { get; init; } = "";
    public string? Title { get; init; }
    public string? Color { get; init; }
}

/// <summary>An "event: change" of /api/events: which projects changed and what kinds of their data.</summary>
public sealed class LiveChange
{
    public bool Registry { get; init; }
    public bool Workspaces { get; init; }
    public bool All { get; init; }

    /// <summary>Knowledge levels whose memories changed (workspace:&lt;name&gt;, user).</summary>
    public IReadOnlyList<string> Levels { get; init; } = [];
    public IReadOnlyList<string> Projects { get; init; } = [];
    public IReadOnlyDictionary<string, IReadOnlyList<string>> Kinds { get; init; } =
        new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);
}

/// <summary>{"error": "..."}, what harness ui answers a failed request with.</summary>
public sealed class ApiError
{
    public string? Error { get; init; }
}
