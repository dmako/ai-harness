using System.Text.Json.Serialization;

namespace Harness.Core.Architecture;

public sealed record FindingSource(
    [property: JsonPropertyName("provider")] string Provider,
    [property: JsonPropertyName("tool"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Tool = null);
public sealed record RuleReference(
    [property: JsonPropertyName("memory")] string Memory,
    [property: JsonPropertyName("line")] string Line = "",
    [property: JsonPropertyName("scope"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Scope = null,
    [property: JsonPropertyName("type"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Type = null);
public sealed record ArchitectureFinding
{
    [JsonPropertyName("kind")] public string Kind { get; init; } = "";
    [JsonPropertyName("level")] public string Level { get; init; } = "";
    [JsonPropertyName("title")] public string Title { get; init; } = "";
    [JsonPropertyName("why")] public string Why { get; init; } = "";
    [JsonPropertyName("fix"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? Fix { get; init; }
    [JsonPropertyName("project"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? Project { get; init; }
    [JsonPropertyName("evidence"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public IReadOnlyList<string>? Evidence { get; init; }
    [JsonPropertyName("key")] public string Key { get; init; } = "";
    [JsonPropertyName("measure")] public int Measure { get; init; }
    [JsonPropertyName("source")] public FindingSource Source { get; init; } = new("");
    [JsonPropertyName("basis")] public string Basis { get; init; } = "";
    [JsonPropertyName("confidence"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] public double Confidence { get; init; }
    [JsonPropertyName("rule"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public RuleReference? Rule { get; init; }
    [JsonPropertyName("rank"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] public double Rank { get; init; }
}
public sealed record LayeredUnit : BuildUnit
{
    public LayeredUnit(BuildUnit unit) : base(unit) { }
    [JsonPropertyName("layer")] public int Layer { get; init; }
    [JsonPropertyName("role"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? Role { get; init; }
    [JsonPropertyName("referenced_by")] public IReadOnlyList<string> ReferencedBy { get; init; } = [];
}
public sealed record ProjectArchitecture(
    [property: JsonPropertyName("language")] string Language,
    [property: JsonPropertyName("noun")] string Noun,
    [property: JsonPropertyName("solutions")] IReadOnlyList<BuildGroup> Solutions,
    [property: JsonPropertyName("projects")] IReadOnlyList<LayeredUnit> Projects,
    [property: JsonPropertyName("smells")] IReadOnlyList<ArchitectureFinding> Findings,
    [property: JsonPropertyName("layers")] int Layers);
