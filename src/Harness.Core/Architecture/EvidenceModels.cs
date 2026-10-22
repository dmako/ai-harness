using System.Text.Json.Serialization;

namespace Harness.Core.Architecture;

public record EvidenceSite(
    [property: JsonPropertyName("path")] string Path,
    [property: JsonPropertyName("line"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] int Line = 0,
    [property: JsonPropertyName("name"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        string? Name = null,
    [property: JsonPropertyName("member"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        string? Member = null
);

public sealed record EvidenceSnippet : EvidenceSite
{
    public EvidenceSnippet(EvidenceSite site)
        : base(site) { }

    [JsonPropertyName("start")]
    public int Start { get; init; }

    [JsonPropertyName("lines")]
    public IReadOnlyList<string>? Lines { get; init; }

    [JsonPropertyName("focus")]
    public int[] Focus { get; init; } = [0, 0];

    [JsonPropertyName("function"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Function { get; init; }

    [JsonPropertyName("truncated"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool Truncated { get; init; }

    [JsonPropertyName("folder"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool Folder { get; init; }
}

public sealed record EvidenceFunction(string Name, int Start, int End);
