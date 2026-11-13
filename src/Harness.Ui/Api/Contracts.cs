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
    public string? Title { get; init; }
    public string Root { get; init; } = "";

    /// <summary>The title, else the root folder's name.</summary>
    [JsonIgnore]
    public string Name
    {
        get
        {
            if (Title is { Length: > 0 } title)
            {
                return title;
            }
            var folder = Root.TrimEnd('\\', '/');
            folder = folder[(folder.LastIndexOfAny(['\\', '/']) + 1)..];
            return folder.Length > 0 ? folder : Id;
        }
    }
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
    public IReadOnlyList<string> Projects { get; init; } = [];
    public IReadOnlyDictionary<string, IReadOnlyList<string>> Kinds { get; init; } =
        new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);
}

/// <summary>{"error": "..."}, what harness ui answers a failed request with.</summary>
public sealed class ApiError
{
    public string? Error { get; init; }
}
