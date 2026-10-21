using System.Text.Json.Serialization;

namespace Harness.Core.Architecture;

public sealed record ChangePartner(
    [property: JsonPropertyName("path")] string Path,
    [property: JsonPropertyName("together")] int Together,
    [property: JsonPropertyName("share")] double Share,
    [property: JsonPropertyName("provider")] string Provider,
    [property: JsonPropertyName("tool")] string Tool);
public sealed record ChangeHotspot(
    [property: JsonPropertyName("path")] string Path,
    [property: JsonPropertyName("commits")] int Commits,
    [property: JsonPropertyName("partners"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<ChangePartner>? Partners = null);
public sealed record MissingChangePartner(
    [property: JsonPropertyName("changed")] string Changed,
    [property: JsonPropertyName("partner")] ChangePartner Partner);
public sealed record ChangePartners(IReadOnlyList<ChangePartner> Partners, IReadOnlyList<string> Errors);
public sealed record ChangeHotspots(IReadOnlyList<ChangeHotspot> Hotspots, int Commits, IReadOnlyList<string> Errors);
public sealed record ChangeCheck(IReadOnlyList<MissingChangePartner> Missing, IReadOnlyList<string> Errors);
public sealed record ChangeCouplingRequest(
    [property: JsonPropertyName("file")] string File = "",
    [property: JsonPropertyName("check")] bool Check = false,
    [property: JsonPropertyName("files")] IReadOnlyList<string>? Files = null,
    [property: JsonPropertyName("limit")] int Limit = 0);
public sealed record ChangeCouplingReport
{
    [JsonPropertyName("partners"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public IReadOnlyList<ChangePartner>? Partners { get; init; }
    [JsonPropertyName("hotspots"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public IReadOnlyList<ChangeHotspot>? Hotspots { get; init; }
    [JsonPropertyName("commits"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] public int Commits { get; init; }
    [JsonPropertyName("changed"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public IReadOnlyList<string>? Changed { get; init; }
    [JsonPropertyName("missing"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public IReadOnlyList<MissingChangePartner>? Missing { get; init; }
    [JsonPropertyName("note"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? Note { get; init; }
}
public interface IChangeCouplingSession
{
    Task<ChangeCouplingReport> ChangeCouplingAsync(ChangeCouplingRequest request, Harness.Core.Providers.IProviderRegistry? registry, CancellationToken token);
}
