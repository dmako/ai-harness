using System.Text.Json.Serialization;
using Harness.Core.Telemetry;

namespace Harness.Core.Reports;

public sealed record KnowledgeUsage
{
    [JsonPropertyName("current_memories")]
    public int Current { get; set; }

    [JsonPropertyName("proposed")]
    public int Proposed { get; set; }

    [JsonPropertyName("approved")]
    public int Approved { get; set; }

    [JsonPropertyName("rejected")]
    public int Rejected { get; set; }

    [JsonPropertyName("pending")]
    public int Pending { get; set; }

    [JsonPropertyName("oldest_pending"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public DateTimeOffset? OldestPending { get; set; }
}

/// <summary>
/// A report finding and who can act on it: the user (configuration, hooks, instructions, reviews) or the harness
/// developer (how harness gathers and serves), whose findings reach only the opt-in telemetry.
/// </summary>
public sealed record ReportFinding(
    string Level,
    string Audience,
    string Area,
    string Message,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Action = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? History = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] ProviderTrend? Trend = null
);

public static class ReportAudience
{
    public const string User = "user";
    public const string Developer = "developer";
}

public sealed record LocalReport
{
    public string Id { get; set; } = "";
    public string Root { get; set; } = "";
    public int Days { get; set; }
    public ContextStatistics Context { get; set; } = new();
    public MetricsReport Metrics { get; set; } = new();
    public IReadOnlyList<ProviderTrend> Trends { get; set; } = [];
    public KnowledgeUsage Knowledge { get; set; } = new();
    public int KnowledgeReads { get; set; }
    public int KnowledgeWrites { get; set; }
    public IReadOnlyList<ReportFinding>? Findings { get; set; }

    /// <summary>
    /// The report as the person using harness sees it: the findings they can act on, without the developer's tuning
    /// numbers (relevance per provider, capability and language; the files no answer held).
    /// </summary>
    public LocalReport ForUser() =>
        this with
        {
            Findings = Findings?.Where(f => f.Audience is not ReportAudience.Developer).ToArray() is { Length: > 0 } own
                ? own
                : null,
            Context = Context with { Rows = null, Providers = [], Misses = null },
        };
}

public sealed record ActivityItem(
    string At,
    string Kind,
    string Title,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Detail = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Ref = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Actor = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Level = null
);
