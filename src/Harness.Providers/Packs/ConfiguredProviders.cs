using Harness.Core.Providers;
using Harness.Providers.Mcp;
using Harness.Providers.Protocol;
using Harness.Providers.Registry;
using Harness.Providers.Search;

namespace Harness.Providers.Packs;

/// <summary>Composes expanded configuration; trusted graphify packs permit explicitly detached warm children.</summary>
public static class ConfiguredProviders
{
    /// <summary>
    /// The registry of the configured providers. With <paramref name="builtInSearch"/> (the MCP server and harness
    /// ui) the built-in search joins them as 'search' when rg or fd is installed, unless [providers.search] turns it
    /// off or an enabled [providers.rg-fd] entry serves text and file search; an explicit [providers.search] entry
    /// is honoured either way. Without rg and fd it is named among the registry's unavailable providers.
    /// </summary>
    public static ProviderRegistry Build(
        IReadOnlyDictionary<string, ProviderConfiguration> providers,
        string root,
        IProviderProcessFactory factory,
        bool light = false,
        IReadOnlyDictionary<string, string?>? environment = null,
        IReadOnlyList<string>? detectedLanguages = null,
        NotIndexedMemo? notIndexed = null,
        bool builtInSearch = false,
        SearchToolset? searchTools = null
    )
    {
        ArgumentNullException.ThrowIfNull(providers);
        var configured = providers.Where(pair => pair.Value.Type is not SearchTools.ProviderType).ToList();
        var explicitSearch = providers.ContainsKey(SearchTools.ProviderName);
        var search = builtInSearch || explicitSearch ? SearchTools.Status(providers, searchTools) : null;
        if (search is { Active: true })
        {
            var settings =
                providers.GetValueOrDefault(SearchTools.ProviderName)
                ?? new ProviderConfiguration { Type = SearchTools.ProviderType };
            configured.Add(new(SearchTools.ProviderName, settings with { Type = SearchTools.ProviderType }));
        }

        var registry = ProviderRouting.Build(
            configured.Select(pair => new ProviderRegistration(
                pair.Key,
                pair.Value.Type,
                () =>
                    pair.Value.Type switch
                    {
                        "harness" => new ProtocolProvider(
                            pair.Key,
                            pair.Value,
                            root,
                            factory,
                            environment,
                            allowDetachedChildren: pair.Value.Pack is "graphify-csharp"
                        ),
                        "mcp" => GenericMcpProvider.Build(
                            pair.Key,
                            pair.Value,
                            root,
                            factory,
                            environment,
                            notIndexed: notIndexed
                        ),
                        SearchTools.ProviderType => new RgFdProvider(
                            pair.Key,
                            search!.Tools!,
                            root,
                            factory,
                            TimeSpan.FromMilliseconds(pair.Value.TimeoutMs),
                            environment
                        ),
                        _ => throw new ArgumentException("unknown provider type " + pair.Value.Type),
                    },
                pair.Value.IsEnabled(),
                pair.Value.InContext(),
                pair.Value.Languages,
                pair.Value.Only,
                TimeSpan.FromMilliseconds(pair.Value.TimeoutMs)
            )),
            root,
            light,
            detectedLanguages
        );
        if (search is { State: SearchTools.Missing })
        {
            registry.AddUnavailable(new(SearchTools.ProviderName, search.Detail, search.Fix));
        }

        return registry;
    }
}
