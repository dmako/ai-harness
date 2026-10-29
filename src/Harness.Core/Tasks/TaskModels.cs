using System.Text.Json;
using System.Text.RegularExpressions;
using Harness.Core.Memory;
using Harness.Core.Serialization;

namespace Harness.Core.Tasks;

public sealed record TaskDefinition
{
    public string Id { get; init; } = "";
    public string Blueprint { get; init; } = "";
    public string Title { get; init; } = "";
    public string Type { get; init; } = "";
    public string Priority { get; init; } = "";
    public string Goal { get; init; } = "";
    public IReadOnlyList<string> Scope { get; init; } = [];
    public IReadOnlyList<string> DependsOn { get; init; } = [];
    public IReadOnlyList<string> Acceptance { get; init; } = [];
    public IReadOnlyList<string> Validation { get; init; } = [];
    public string Notes { get; init; } = "";
    public string Status { get; init; } = "";

    public TaskDefinition Normalize() =>
        this with
        {
            Id = Id.Trim(),
            Title = Title.Trim(),
            Blueprint = Blueprint.Trim(),
            Type = Type.Length is 0 ? "feature" : Type,
            Priority = Priority.Length is 0 ? "medium" : Priority,
            Scope = Scope.Where(p => p.Length is not 0).Distinct(StringComparer.Ordinal).ToArray(),
            DependsOn = DependsOn.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray(),
        };

    public void Validate()
    {
        if (!TaskWorkflow.ValidId(Id))
        {
            throw new ArgumentException($"invalid task id {TaskWorkflow.Quote(Id)} (want e.g. HAR-0024)");
        }

        if (Title.Length is 0)
        {
            throw new ArgumentException($"{Id}: title is required");
        }

        if (!TaskWorkflow.Types.Contains(Type, StringComparer.Ordinal))
        {
            throw new ArgumentException(
                $"{Id}: invalid type {TaskWorkflow.Quote(Type)} (want one of {string.Join(", ", TaskWorkflow.Types)})"
            );
        }

        if (TaskWorkflow.PriorityRank(Priority) is 4)
        {
            throw new ArgumentException(
                $"{Id}: invalid priority {TaskWorkflow.Quote(Priority)} (want low, medium, high or critical)"
            );
        }

        if (DependsOn.Contains(Id, StringComparer.Ordinal))
        {
            throw new ArgumentException($"{Id}: depends on itself");
        }

        if (Status is not ("" or "backlog" or "ready" or "done" or "blocked"))
        {
            throw new ArgumentException(
                $"{Id}: initial status {TaskWorkflow.Quote(Status)} not allowed in a backlog file (use backlog, done or blocked)"
            );
        }
    }
}

public sealed record BlueprintDefinition(
    string Id,
    string Title,
    string Goal = "",
    IReadOnlyList<string>? Exit = null,
    string Notes = ""
);

/// <summary>What removing a blueprint did to its tasks: moved to another (or none), or deleted, and the outside tasks
/// that lost a dependency on a deleted one.</summary>
public sealed record BlueprintRemoval(
    string Id,
    IReadOnlyList<string> Moved,
    IReadOnlyList<string> Deleted,
    IReadOnlyList<string> Unlinked
);

/// <summary>What happens to a removed blueprint's tasks; Keep removes only a blueprint that has none.</summary>
public enum BlueprintTasks
{
    Keep,
    Move,
    Delete,
}

public sealed record BacklogFile(
    int Version,
    IReadOnlyList<TaskDefinition> Tasks,
    IReadOnlyList<BlueprintDefinition> Blueprints
);

public sealed record TaskDependency(string Id, string Status);

public sealed record TaskCriterion(int Index, string Criterion, string Result, string Evidence);

public sealed record TaskCheck(string Command, string Result, string Output = "");

public sealed record TaskEvent(string Event, string From, string To, string Actor, string Detail, MemoryTimestamp At);

public sealed record TaskActor(string Name, string Token = "", bool Privileged = false)
{
    public string AuditName => string.IsNullOrWhiteSpace(Name) ? "unknown" : Name;
}

public sealed record TaskOrigin(string By, string Source, string Detail);

public sealed record TaskFilter(IReadOnlyList<string>? Statuses = null, string Blueprint = "", int Limit = 0);

public sealed record TaskImportResult(
    IReadOnlyList<string> Added,
    IReadOnlyList<string> Updated,
    IReadOnlyList<string> Unchanged,
    IReadOnlyList<string> Orphaned,
    IReadOnlyList<string> Unlocked
);

public sealed record TaskExportResult(string Path, IReadOnlyList<string> Added, bool Created);

public sealed record BlueprintProgress(
    BlueprintDefinition Definition,
    bool Defined,
    IReadOnlyDictionary<string, int> Tasks
)
{
    public int Total => Tasks.Values.Sum();
    public bool Done => Total > 0 && Tasks.GetValueOrDefault("done") == Total;
}

public sealed record TaskRecord
{
    public required TaskDefinition Definition { get; init; }
    public string Id => Definition.Id;
    public required string Status { get; init; }
    public string BlockedReason { get; init; } = "";
    public string Claimant { get; init; } = "";

    // Storage authorization data must never be serialized into public views.
    [System.Text.Json.Serialization.JsonIgnore]
    public string ClaimToken { get; init; } = "";
    public MemoryTimestamp? ClaimedAt { get; init; }
    public MemoryTimestamp? LeaseUntil { get; init; }
    public string CompletionSummary { get; init; } = "";
    public string CompletionCommit { get; init; } = "";
    public IReadOnlyList<TaskCheck> CompletionChecks { get; init; } = [];
    public string CompletedBy { get; init; } = "";
    public MemoryTimestamp? CompletedAt { get; init; }
    public IReadOnlyList<TaskDependency> Dependencies { get; init; } = [];
    public IReadOnlyList<TaskCriterion> Criteria { get; init; } = [];
    public string Source { get; init; } = "";
    public int Ordinal { get; init; }
    public MemoryTimestamp CreatedAt { get; init; }
    public MemoryTimestamp UpdatedAt { get; init; }

    public bool LeaseExpired(MemoryTimestamp at) =>
        TaskWorkflow.HoldsClaim(Status) && LeaseUntil is { } until && until.CompareTo(at) <= 0;

    public TaskDefinition ExportDefinition() =>
        Definition with
        {
            Status = Status is "done" or "blocked" ? Status : "",
            DependsOn = Dependencies.Select(d => d.Id).ToArray(),
            Acceptance = Criteria.Select(c => c.Criterion).ToArray(),
        };
}

public static partial class TaskWorkflow
{
    public static IReadOnlyList<string> Statuses { get; } =
    ["backlog", "ready", "claimed", "in_progress", "review", "done", "blocked", "deferred"];
    public static IReadOnlyList<string> Types { get; } =
    ["feature", "bug", "chore", "spike", "docs", "test", "process"];

    public static bool ValidId(string id) => IdPattern.IsMatch(id);

    public static string ParseStatus(string value)
    {
        return Statuses.FirstOrDefault(s => string.Equals(s, value.Trim(), StringComparison.OrdinalIgnoreCase))
            ?? throw new ArgumentException(
                $"invalid status {Quote(value)} (want one of: {string.Join(", ", Statuses)})"
            );
    }

    public static bool HoldsClaim(string status) => status is "claimed" or "in_progress" or "review";

    public static string NormalizeResult(string value) =>
        string.Concat(value.Trim().EnumerateRunes().Select(r => UnicodeCharacters.ToLower(r).ToString()));

    public static int PriorityRank(string priority) =>
        priority switch
        {
            "critical" => 0,
            "high" => 1,
            "medium" => 2,
            "low" => 3,
            _ => 4,
        };

    public static bool CanTransition(string from, string to) =>
        from switch
        {
            "backlog" => to is "ready" or "blocked" or "deferred",
            "ready" => to is "claimed" or "backlog" or "blocked" or "deferred",
            "claimed" => to is "in_progress" or "review" or "ready" or "done" or "blocked",
            "in_progress" => to is "review" or "ready" or "done" or "blocked",
            "review" => to is "in_progress" or "done" or "blocked",
            "blocked" => to is "backlog" or "ready" or "deferred",
            "deferred" => to is "backlog" or "ready",
            _ => false,
        };

    public static string Quote(string value) => HarnessJson.Serialize(value, QuoteOptions);

    private static readonly JsonSerializerOptions QuoteOptions = new()
    {
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    [GeneratedRegex(@"\A[A-Z][A-Z0-9]*-[0-9A-Za-z][0-9A-Za-z._-]*\z", RegexOptions.CultureInvariant, 1000)]
    private static partial Regex IdPattern { get; }
}
