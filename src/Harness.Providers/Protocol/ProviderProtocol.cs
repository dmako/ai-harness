using System.Text.Json;
using System.Text.Json.Serialization;
using Harness.Core.Providers;

namespace Harness.Providers.Protocol;

public static class ProviderProtocol
{
    public const string Version = "harness-provider/1";
    public const string InfoTool = "harness_provider_info";
    public const string RefreshTool = "harness_refresh";

    public static bool Compatible(string protocol) =>
        protocol == Version || protocol.StartsWith(Version + ".", StringComparison.Ordinal);

    public static string ToolFor(string capability) => capability.Replace('.', '_');

    internal static JsonSerializerOptions JsonOptions { get; } =
        new()
        {
            PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingDefault,
        };
}

public sealed record ProviderInfo(
    string Protocol = "",
    string Name = "",
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)]
        IReadOnlyList<ProtocolCapability>? Capabilities = null,
    bool Refresh = false
);

public sealed record ProtocolCapability(
    string Capability = "",
    string? Freshness = null,
    string? Cost = null,
    string? Latency = null,
    string? Evidence = null
)
{
    public string? Freshness
    {
        get;
        init => field = Empty(value);
    } = Empty(Freshness);
    public string? Cost
    {
        get;
        init => field = Empty(value);
    } = Empty(Cost);
    public string? Latency
    {
        get;
        init => field = Empty(value);
    } = Empty(Latency);
    public string? Evidence
    {
        get;
        init => field = Empty(value);
    } = Empty(Evidence);

    private static string? Empty(string? value) => string.IsNullOrEmpty(value) ? null : value;
}

public sealed record ProtocolResponse(
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] IReadOnlyList<ProviderItem>? Items = null,
    bool Truncated = false,
    IReadOnlyDictionary<string, JsonElement>? Summary = null,
    string? Text = null
)
{
    public string? Text
    {
        get;
        init => field = string.IsNullOrEmpty(value) ? null : value;
    } = string.IsNullOrEmpty(Text) ? null : Text;
}
