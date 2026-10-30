using System.Text.Json.Serialization;

namespace Harness.Core.Telemetry;

public sealed record ToolMetrics
{
    [JsonPropertyName("tool")]
    public string Tool { get; set; } = "";

    [JsonPropertyName("kind"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Kind { get; set; } = null;

    [JsonPropertyName("calls")]
    public int Calls { get; set; } = default;

    [JsonPropertyName("errors")]
    public int Errors { get; set; } = default;

    [JsonPropertyName("empty")]
    public int Empty { get; set; } = default;

    [JsonPropertyName("error_rate")]
    public double ErrorRate { get; set; } = default;

    [JsonPropertyName("empty_rate")]
    public double EmptyRate { get; set; } = default;

    [JsonPropertyName("p50_ms")]
    public long P50MS { get; set; } = default;

    [JsonPropertyName("p95_ms")]
    public long P95MS { get; set; } = default;

    [JsonPropertyName("avg_bytes")]
    public int AvgBytes { get; set; } = default;

    [JsonPropertyName("clients"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<string>? Clients { get; set; } = null;

    [JsonPropertyName("last_at")]
    public string Last { get; set; } = "";
}

public sealed record ProviderMetrics
{
    [JsonPropertyName("provider")]
    public string Provider { get; set; } = "";

    [JsonPropertyName("capability")]
    public string Capability { get; set; } = "";

    [JsonPropertyName("calls")]
    public int Calls { get; set; } = default;

    [JsonPropertyName("errors")]
    public int Errors { get; set; } = default;

    [JsonPropertyName("timeouts")]
    public int Timeouts { get; set; } = default;

    [JsonPropertyName("empty")]
    public int Empty { get; set; } = default;

    [JsonPropertyName("error_rate")]
    public double ErrorRate { get; set; } = default;

    [JsonPropertyName("empty_rate")]
    public double EmptyRate { get; set; } = default;

    [JsonPropertyName("p50_ms")]
    public long P50MS { get; set; } = default;

    [JsonPropertyName("p95_ms")]
    public long P95MS { get; set; } = default;

    [JsonPropertyName("context_hit_share")]
    public double HitShare { get; set; } = default;

    [JsonPropertyName("last_at")]
    public string Last { get; set; } = "";
}

public sealed record MetricsReport
{
    [JsonPropertyName("since")]
    public DateTimeOffset Since { get; set; } = default;

    [JsonPropertyName("tools")]
    public IReadOnlyList<ToolMetrics> Tools { get; set; } = [];

    [JsonPropertyName("providers")]
    public IReadOnlyList<ProviderMetrics> Providers { get; set; } = [];

    [JsonPropertyName("hooks")]
    public HookSummary Hooks { get; set; } = new();

    [JsonPropertyName("bypasses"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<ProviderBypass>? Bypasses { get; set; } = null;
}

public sealed record HookSummary
{
    [JsonPropertyName("events")]
    public int Events { get; set; } = default;

    [JsonPropertyName("sessions")]
    public int Sessions { get; set; } = default;

    [JsonPropertyName("clients"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<string>? Clients { get; set; } = null;

    [JsonPropertyName("last_at"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Last { get; set; } = null;
}

public sealed record ProviderBypass
{
    [JsonPropertyName("provider")]
    public string Provider { get; set; } = "";

    [JsonPropertyName("server")]
    public string Server { get; set; } = "";

    [JsonPropertyName("tool")]
    public string Tool { get; set; } = "";

    [JsonPropertyName("calls")]
    public int Calls { get; set; } = default;

    [JsonPropertyName("last_at")]
    public string Last { get; set; } = "";
}

public sealed record AfterRead
{
    [JsonPropertyName("calls")]
    public int Calls { get; set; } = default;

    [JsonPropertyName("linked")]
    public int Linked { get; set; } = default;

    [JsonPropertyName("touched")]
    public int Touched { get; set; } = default;

    [JsonPropertyName("hits")]
    public int Hits { get; set; } = default;

    [JsonPropertyName("hit_rate")]
    public double HitRate { get; set; } = default;

    [JsonPropertyName("edited")]
    public int Edited { get; set; } = default;

    [JsonPropertyName("edit_hits")]
    public int EditHits { get; set; } = default;
}

public sealed record ProviderDay
{
    [JsonPropertyName("day")]
    public string Day { get; set; } = "";

    [JsonPropertyName("calls")]
    public int Calls { get; set; } = default;

    [JsonPropertyName("errors")]
    public int Errors { get; set; } = default;

    [JsonPropertyName("slow")]
    public int Slow { get; set; } = default;
}

public sealed record ProviderTrend
{
    [JsonPropertyName("provider")]
    public string Provider { get; set; } = "";

    [JsonPropertyName("capability")]
    public string Capability { get; set; } = "";

    [JsonPropertyName("days")]
    public IReadOnlyList<ProviderDay> Days { get; set; } = [];

    [JsonPropertyName("last_call_at")]
    public string LastCall { get; set; } = "";

    [JsonPropertyName("last_error_at"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? LastError { get; set; } = null;

    [JsonPropertyName("calls_after_error")]
    public int CallsAfterError { get; set; } = default;

    [JsonPropertyName("last_slow_at"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? LastSlow { get; set; } = null;

    [JsonPropertyName("calls_after_slow")]
    public int CallsAfterSlow { get; set; } = default;
}

public sealed record ContextProviderRow
{
    [JsonPropertyName("provider")]
    public string Provider { get; set; } = "";

    [JsonPropertyName("capability")]
    public string Capability { get; set; } = "";

    [JsonPropertyName("language")]
    public string Language { get; set; } = "";

    [JsonPropertyName("hits")]
    public int Hits { get; set; } = default;

    [JsonPropertyName("changed")]
    public int Changed { get; set; } = default;

    [JsonPropertyName("hit_rate")]
    public double HitRate { get; set; } = default;

    [JsonPropertyName("hits_tier0")]
    public int BestTier0 { get; set; } = default;

    [JsonPropertyName("median_position")]
    public int MedianPos { get; set; } = default;
}

public sealed record ContextMiss
{
    [JsonPropertyName("path")]
    public string Path { get; set; } = "";

    [JsonPropertyName("language")]
    public string Language { get; set; } = "";

    [JsonPropertyName("times")]
    public int Times { get; set; } = default;

    [JsonPropertyName("called_from"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? CalledFrom { get; set; } = null;
}

public sealed record ContextStatistics
{
    [JsonPropertyName("calls")]
    public int Calls { get; set; } = default;

    [JsonPropertyName("open")]
    public int Open { get; set; } = default;

    [JsonPropertyName("changed")]
    public int Changed { get; set; } = default;

    [JsonPropertyName("hits")]
    public int Hits { get; set; } = default;

    [JsonPropertyName("hit_rate")]
    public double HitRate { get; set; } = default;

    [JsonPropertyName("tests")]
    public int[] Tests { get; set; } = [0, 0];

    [JsonPropertyName("callee_misses")]
    public int CalleeMisses { get; set; } = default;

    [JsonPropertyName("by_provider")]
    public IReadOnlyList<ContextProviderRow>? Rows { get; set; } = null;

    [JsonPropertyName("providers")]
    public Dictionary<string, int[]> Providers { get; set; } = [];

    [JsonPropertyName("top_misses")]
    public IReadOnlyList<ContextMiss>? Misses { get; set; } = null;

    [JsonPropertyName("by_language")]
    public Dictionary<string, int[]> Language { get; set; } = [];

    [JsonPropertyName("after_read")]
    public AfterRead AfterRead { get; set; } = new();
}

public sealed record ToolObservation(
    DateTimeOffset At,
    string Tool,
    string Kind,
    string Client,
    long DurationMs,
    int Bytes,
    bool Empty,
    string Error = ""
);

public sealed record ProviderObservation(
    DateTimeOffset At,
    string Provider,
    string Capability,
    string Query,
    int Items,
    int Bytes,
    long DurationMs,
    bool Timeout,
    string Error = ""
);

public sealed record ContextUnitObservation(
    string Path,
    string Name,
    string Role,
    string Provider,
    string Capability,
    int Rank = 0,
    int Position = 0,
    int Tier = 0,
    bool Inlined = false
);

public sealed record ContextObservation(
    string WorktreeId,
    string Head,
    string Task,
    int Budget,
    int Used,
    IReadOnlyList<ContextUnitObservation> Units
);

public sealed record ClientToolEvent(
    DateTimeOffset At,
    string Client,
    string Session,
    string WorktreeId,
    string Tool,
    string Kind,
    string McpServer = "",
    string McpTool = "",
    IReadOnlyList<string>? Paths = null,
    string Task = ""
);

public sealed record ContextResolution(int Calls, int Changed, int Hits, int Callees, int Expired);

public interface IToolObserver
{
    void Observe(ToolObservation observation);
}

public interface IContextObserver
{
    void Observe(ContextObservation observation);
}

/// <summary>
/// One day of a project's headline numbers: the get_context answers scored that day with their mean hit rate (tests
/// apart, as the context statistics count them) and the tasks completed.
/// </summary>
public sealed record TrendDay
{
    [JsonPropertyName("day")]
    public string Day { get; set; } = "";

    [JsonPropertyName("answers")]
    public int Answers { get; set; }

    [JsonPropertyName("hit_rate")]
    public double HitRate { get; set; }

    [JsonPropertyName("done")]
    public int Done { get; set; }
}
