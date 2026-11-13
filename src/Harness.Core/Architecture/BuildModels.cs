using System.Text.Json.Serialization;

namespace Harness.Core.Architecture;

public sealed record BuildPackage(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("version"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        string? Version = null,
    [property: JsonPropertyName("frameworks"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        IReadOnlyList<string>? Frameworks = null,
    [property: JsonPropertyName("conditional"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
        bool Conditional = false,
    [property: JsonPropertyName("unresolved"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
        bool Unresolved = false
);

public record BuildUnit
{
    [JsonPropertyName("name")]
    public string Name { get; init; } = "";

    [JsonPropertyName("path")]
    public string Path { get; init; } = "";

    [JsonPropertyName("package"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Package { get; init; }

    [JsonPropertyName("version"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Version { get; init; }

    [JsonPropertyName("kind")]
    public string Kind { get; set; } = "library";

    [JsonPropertyName("references")]
    public List<string> References { get; } = [];

    [JsonPropertyName("ref_at"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public Dictionary<string, string>? ReferenceLocations { get; set; }

    /// <summary>
    /// Build-order-only references (MSBuild ReferenceOutputAssembly=false): built before this unit, not used as
    /// code, so they stay out of References and of layers, cycles and the layering rules.
    /// </summary>
    [JsonPropertyName("build_order"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<string>? BuildOrder { get; set; }

    /// <summary>References a static read cannot resolve: the expression as evaluated, with where it is declared.</summary>
    [JsonPropertyName("unresolved_refs"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public Dictionary<string, string>? UnresolvedReferences { get; set; }

    [JsonPropertyName("packages")]
    public List<BuildPackage> Packages { get; } = [];

    [JsonPropertyName("solutions")]
    public List<string> Solutions { get; } = [];
}

public sealed record BuildGroup(
    [property: JsonPropertyName("path")] string Path,
    [property: JsonPropertyName("projects")] IReadOnlyList<string> Projects,
    [property: JsonPropertyName("missing"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        IReadOnlyList<string>? Missing = null
);

public sealed record ProjectBuild(
    [property: JsonPropertyName("language")] string Language,
    [property: JsonPropertyName("noun")] string Noun,
    [property: JsonPropertyName("solutions")] IReadOnlyList<BuildGroup> Solutions,
    [property: JsonPropertyName("projects")] IReadOnlyList<BuildUnit> Projects
)
{
    [JsonIgnore]
    public IReadOnlyList<ArchitectureFinding> Extra { get; init; } = [];
}
