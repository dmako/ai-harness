using Harness.Core.Providers;
using Harness.Providers.Mcp;
using Harness.Providers.Protocol;
using Harness.Providers.Registry;

namespace Harness.Providers.Packs;

/// <summary>Composes expanded configuration; trusted graphify packs permit explicitly detached warm children.</summary>
public static class ConfiguredProviders
{
    public static ProviderRegistry Build(IReadOnlyDictionary<string, ProviderConfiguration> providers, string root, IProviderProcessFactory factory,
        bool light = false, IReadOnlyDictionary<string, string?>? environment = null, IReadOnlyList<string>? detectedLanguages = null) =>
        ProviderRouting.Build(providers.Select(pair => new ProviderRegistration(pair.Key, pair.Value.Type,
            () => pair.Value.Type switch
            {
                "harness" => new ProtocolProvider(pair.Key, pair.Value, root, factory, environment, allowDetachedChildren: pair.Value.Pack is "graphify-csharp"),
                "mcp" => GenericMcpProvider.Build(pair.Key, pair.Value, root, factory, environment),
                _ => throw new ArgumentException("unknown provider type " + pair.Value.Type),
            }, pair.Value.IsEnabled(), pair.Value.InContext(), pair.Value.Languages, pair.Value.Only, TimeSpan.FromMilliseconds(pair.Value.TimeoutMs))), root, light, detectedLanguages);
}
