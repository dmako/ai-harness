using System.Text.Json.Serialization;

namespace Harness.Core.Architecture;

public sealed record SystemNode(string Id, string Name, string Kind, string? MemberId = null, string? Detail = null)
{
    [JsonPropertyName("id")] public string Id { get; init; } = Id;
    [JsonPropertyName("name")] public string Name { get; init; } = Name;
    [JsonPropertyName("kind")] public string Kind { get; init; } = Kind;
    [JsonPropertyName("member_id"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? MemberId { get; init; } = MemberId;
    [JsonPropertyName("detail"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? Detail { get; set; } = Detail;
    [JsonPropertyName("layer")] public int Layer { get; set; }
}
public sealed record SystemLink(string From, string To, string Style)
{
    [JsonPropertyName("from")] public string From { get; init; } = From;
    [JsonPropertyName("to")] public string To { get; init; } = To;
    [JsonPropertyName("style")] public string Style { get; init; } = Style;
    [JsonPropertyName("what")] public List<string> What { get; init; } = [];
    [JsonPropertyName("contracts"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public List<string>? Contracts { get; set; }
    [JsonPropertyName("uses")] public int Uses { get; set; }
    [JsonPropertyName("evidence")] public List<string> Evidence { get; init; } = [];
    [JsonPropertyName("basis"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? Basis { get; set; }
}
public sealed record SystemContract(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("owner")] string Owner,
    [property: JsonPropertyName("declared")] string Declared,
    [property: JsonPropertyName("servers")] IReadOnlyList<string> Servers,
    [property: JsonPropertyName("clients")] IReadOnlyList<string> Clients);
public sealed record WorkspaceSystem(
    [property: JsonPropertyName("nodes")] IReadOnlyList<SystemNode> Nodes,
    [property: JsonPropertyName("links")] IReadOnlyList<SystemLink> Links,
    [property: JsonPropertyName("contracts")] IReadOnlyList<SystemContract> Contracts,
    [property: JsonPropertyName("smells")] IReadOnlyList<ArchitectureFinding> Findings,
    [property: JsonPropertyName("notes"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<string>? Notes = null);
