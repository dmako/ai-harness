using System.Text.Json.Serialization;

namespace Harness.Ui.Api;

// The projects area: GET /api/projects (UiProjectCatalog with ProjectOverview's live state). A member marked
// required is always in the server's answer: its absence (a renamed field) fails deserialisation instead of
// showing an empty page. CommittedUiFixturesMatchTheServersShapes and UiContractTests pin it.

/// <summary>GET /api/projects.</summary>
public sealed class ProjectList
{
    public required IReadOnlyList<ProjectInfo> Projects { get; init; }
}

/// <summary>A registered project as harness ui lists it.</summary>
public sealed class ProjectInfo
{
    public required string Id { get; init; }

    /// <summary>The display name chosen with rename, if any.</summary>
    public string? Title { get; init; }

    /// <summary>The short name chosen with rename (commands and agents use it), if any.</summary>
    public string? Name { get; init; }

    public required string Root { get; init; }
    public required ProjectLive Live { get; init; }

    /// <summary>The short names of the workspaces it belongs to (absent when none).</summary>
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

    /// <summary>The compact mark shown beside the project name.</summary>
    [JsonIgnore]
    public string Mark => MarkFor(DisplayName);

    [JsonIgnore]
    public string MarkColor => MarkColorFor(Id);

    public static string MarkColorFor(string id)
    {
        uint hash = 0;
        foreach (var c in id)
        {
            hash = unchecked((hash * 31) + c);
        }
        return (hash % 8).ToString(System.Globalization.CultureInfo.InvariantCulture);
    }

    public static string MarkFor(string name)
    {
        var words = name.Split([' ', '.', '_', '-'], StringSplitOptions.RemoveEmptyEntries)
            .Where(w => !new[] { "webapi", "api", "web", "service", "services", "app" }
                .Contains(w, StringComparer.OrdinalIgnoreCase)).ToArray();
        if (words.Length >= 2)
        {
            return string.Concat(words[0][0], words[1][0]).ToUpperInvariant();
        }
        var word = words.FirstOrDefault() ?? name;
        var capitals = word.Where(char.IsUpper).Take(2).ToArray();
        return (capitals.Length == 2 ? new string(capitals) : word[..Math.Min(2, word.Length)]).ToUpperInvariant();
    }

    /// <summary>How many things wait for a person here (app.js waitingIn): proposals, failing checks, unreadable state.</summary>
    [JsonIgnore]
    public int Waiting => Live.KnowledgeWaiting + Live.WorkProposals + Live.ChecksFailed + (Live.State is "ok" ? 0 : 1);
}

/// <summary>A project's live state (ProjectOverview.InspectAsync): readable or not, what waits, and its code's state.</summary>
public sealed class ProjectLive
{
    /// <summary>ok, missing, error, migrate_needed, newer_schema…</summary>
    public required string State { get; init; }

    /// <summary>Why the code index is stale ("" or absent while it is fresh): a reason, not a flag.</summary>
    public string? Stale { get; init; }
    public string? Detail { get; init; }
    public required int PendingProposals { get; init; }
    public required int PendingArchProposals { get; init; }
    public required int PendingTaskProposals { get; init; }
    public required int PendingBlueprintProposals { get; init; }
    public required int RemovedFromProject { get; init; }
    public required int ChecksFailed { get; init; }
    public required int ChecksWarned { get; init; }
    public string? Branch { get; init; }
    public string? Commit { get; init; }
    public string? SyncedAt { get; init; }

    /// <summary>get_context answers scored in 30 days, and the share that hit.</summary>
    [JsonPropertyName("context_answers_30d")]
    public int ContextAnswers30d { get; init; }

    [JsonPropertyName("context_hit_rate_30d")]
    public double ContextHitRate30d { get; init; }

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
            "ok" => Stale is { Length: > 0 } ? "stale" : "ok",
            "missing" or "error" => "bad",
            _ => "warn",
        };

    [JsonIgnore]
    public string HealthKind => (State, ChecksFailed, Stale, ChecksWarned) switch
    {
        (not "ok", _, _, _) => Kind,
        (_, > 0, _, _) => "bad",
        (_, _, { Length: > 0 }, _) or (_, _, _, > 0) => "warn",
        _ => "ok",
    };

    [JsonIgnore]
    public string HealthWords => (State, ChecksFailed, Stale, ChecksWarned) switch
    {
        (not "ok", _, _, _) => Words,
        (_, > 0, _, _) => $"{HealthCount(ChecksFailed, "check")} failing",
        (_, _, { Length: > 0 }, _) => "index stale",
        (_, _, _, > 0) => HealthCount(ChecksWarned, "warning"),
        _ => "healthy",
    };

    private static string HealthCount(int count, string word) => $"{count} {word}{(count == 1 ? "" : "s")}";

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

/// <summary>POST /api/projects/{id}/rename and /api/workspaces/{name}/rename: what changes; null stays.</summary>
public sealed class RenameChange
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Title { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Name { get; init; }
}
