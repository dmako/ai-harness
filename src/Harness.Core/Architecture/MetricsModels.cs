using System.Text.Json.Serialization;

namespace Harness.Core.Architecture;

/// <summary>One member as Microsoft.CodeAnalysis.Metrics measured it; Line is the line of its name.</summary>
public sealed record MeasuredMember(
    [property: JsonPropertyName("path")] string Path,
    [property: JsonPropertyName("line")] int Line,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("kind")] string Kind,
    [property: JsonPropertyName("complexity")] int Complexity,
    [property: JsonPropertyName("maintainability")] int Maintainability,
    [property: JsonPropertyName("project")] string Project
);

/// <summary>A background Microsoft.CodeAnalysis.Metrics run for one commit (running, done, failed or superseded).</summary>
public sealed record MetricsRun
{
    [JsonPropertyName("commit")]
    public string Commit { get; init; } = "";

    [JsonPropertyName("state")]
    public string State { get; init; } = "";

    [JsonPropertyName("tool")]
    public string Tool { get; init; } = "";

    [JsonPropertyName("started_at")]
    public string StartedAt { get; init; } = "";

    [JsonPropertyName("finished_at"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? FinishedAt { get; init; }

    [JsonPropertyName("pid"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public int Pid { get; init; }

    /// <summary>sdk, restore, load, platform, timeout or worker: what the fix is.</summary>
    [JsonPropertyName("failure"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Failure { get; init; }

    [JsonPropertyName("error"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Error { get; init; }

    /// <summary>The earlier run whose members were kept for projects without changes.</summary>
    [JsonPropertyName("base"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Base { get; init; }

    [JsonPropertyName("measured")]
    public IReadOnlyList<string> Measured { get; init; } = [];

    [JsonPropertyName("reused")]
    public IReadOnlyList<string> Reused { get; init; } = [];

    [JsonPropertyName("members")]
    public IReadOnlyList<MeasuredMember> Members { get; init; } = [];
}

/// <summary>The exact-complexity worker as provider_status and doctor show it.</summary>
public sealed record ComplexityMetricsStatus(string State, string Detail, string? Fix = null, string? Commit = null);
