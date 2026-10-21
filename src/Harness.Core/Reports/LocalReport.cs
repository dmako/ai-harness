using System.Text.Json.Serialization;
using Harness.Core.Telemetry;

namespace Harness.Core.Reports;

public sealed record KnowledgeUsage
{
    [JsonPropertyName("current_memories")] public int Current { get; set; }
    [JsonPropertyName("proposed")] public int Proposed { get; set; }
    [JsonPropertyName("approved")] public int Approved { get; set; }
    [JsonPropertyName("rejected")] public int Rejected { get; set; }
    [JsonPropertyName("pending")] public int Pending { get; set; }
    [JsonPropertyName("oldest_pending"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public DateTimeOffset? OldestPending { get; set; }
}
public sealed record ReportFinding(string Level, string Area, string Message, [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Action = null, [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? History = null, [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] ProviderTrend? Trend = null);
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
}

public sealed record ActivityItem(string At, string Kind, string Title,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Detail = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Ref = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Actor = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Level = null);
