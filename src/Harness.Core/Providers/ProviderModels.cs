using System.Text.Json;
using System.Text.Json.Serialization;

namespace Harness.Core.Providers;

public sealed record CapabilityInfo(
    [property: JsonPropertyName("capability")] string Capability,
    [property: JsonPropertyName("provider")] string Provider,
    [property: JsonPropertyName("cost")] string Cost = "",
    [property: JsonPropertyName("latency")] string Latency = "",
    [property: JsonPropertyName("freshness")] string Freshness = "",
    [property: JsonPropertyName("scope")] string Scope = "",
    string? Evidence = null)
{
    [JsonPropertyName("evidence"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Evidence { get; init => field = string.IsNullOrEmpty(value) ? null : value; } = string.IsNullOrEmpty(Evidence) ? null : Evidence;

    public string EvidenceOf() => string.IsNullOrEmpty(Evidence) ? Capabilities.DefaultEvidence(Capability) : Evidence;
}

public sealed record ProviderRequest(string Capability, string Query = "", string Path = "", int Limit = 0,
    string Target = "", int Budget = 0, string Package = "", string Version = "", string Type = "")
{
    // Segment boundaries matter: src must not match src2. Providers own limit/budget enforcement.
    public bool Covers(string file)
    {
        var prefix = Path.Replace(System.IO.Path.DirectorySeparatorChar, '/');
        prefix = prefix.EndsWith('/') ? prefix[..^1] : prefix;
        return Path.Length is 0 || file == prefix || file.StartsWith(prefix + "/", StringComparison.Ordinal);
    }
}

public sealed record ProviderReference(
    string? Name = null,
    string? Path = null,
    [property: JsonPropertyName("start_line"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] int StartLine = 0)
{
    [JsonPropertyName("name"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Name { get; init => field = string.IsNullOrEmpty(value) ? null : value; } = string.IsNullOrEmpty(Name) ? null : Name;
    [JsonPropertyName("path"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Path { get; init => field = string.IsNullOrEmpty(value) ? null : value; } = string.IsNullOrEmpty(Path) ? null : Path;
}

public sealed record ProviderItem
{
    [JsonPropertyName("kind")] public string Kind { get; init; } = "";
    [JsonPropertyName("name"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? Name { get; init => field = string.IsNullOrEmpty(value) ? null : value; }
    [JsonPropertyName("qualified_name"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? QualifiedName { get; init => field = string.IsNullOrEmpty(value) ? null : value; }
    [JsonPropertyName("label"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? Label { get; init => field = string.IsNullOrEmpty(value) ? null : value; }
    [JsonPropertyName("path"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? Path { get; init => field = string.IsNullOrEmpty(value) ? null : value; }
    [JsonPropertyName("start_line"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] public int StartLine { get; init; }
    [JsonPropertyName("end_line"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] public int EndLine { get; init; }
    [JsonPropertyName("snippet"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? Snippet { get; init => field = string.IsNullOrEmpty(value) ? null : value; }
    [JsonPropertyName("score"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] public double Score { get; init; }
    [JsonPropertyName("depth"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] public int Depth { get; init; }
    [JsonPropertyName("related"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public ProviderReference? Related { get; init; }
    [JsonPropertyName("members"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public IReadOnlyList<string>? Members { get; init => field = value is { Count: > 0 } ? value : null; }
    [JsonPropertyName("count"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] public int Count { get; init; }
    [JsonPropertyName("confidence"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] public double Confidence { get; init; }
    [JsonPropertyName("provider")] public string Provider { get; init; } = "";
    [JsonPropertyName("tool")] public string Tool { get; init; } = "";
}

public sealed record ProviderResult
{
    public string Provider { get; init; } = "";
    public string Capability { get; init; } = "";
    public IReadOnlyList<ProviderItem> Items { get; init; } = [];
    public IReadOnlyDictionary<string, JsonElement>? Summary { get; init; }
    public string Text { get; init; } = "";
    public bool Truncated { get; init; }
    public TimeSpan Latency { get; init; }
    public int Bytes { get; init; }
    public bool TimedOut { get; init; }
    public Exception? Error { get; init; }
}

public sealed record ProviderHealth(
    [property: JsonPropertyName("state")] string State,
    string? Detail = null,
    string? Fix = null)
{
    [JsonPropertyName("detail"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Detail { get; init => field = string.IsNullOrEmpty(value) ? null : value; } = string.IsNullOrEmpty(Detail) ? null : Detail;
    [JsonPropertyName("fix"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Fix { get; init => field = string.IsNullOrEmpty(value) ? null : value; } = string.IsNullOrEmpty(Fix) ? null : Fix;
}

public sealed record RefreshRequest(bool Full = false, IReadOnlyList<string>? Changed = null);
public sealed record RefreshResult(bool Skipped = false, string Detail = "");
