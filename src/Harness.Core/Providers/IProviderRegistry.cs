using System.Text.Json;

namespace Harness.Core.Providers;

public interface IProviderRegistry
{
    Task DiscoverAsync(CancellationToken cancellationToken = default);
    IReadOnlyList<IProvider> Providers();
    IReadOnlyList<CapabilityInfo> Capabilities();
    IReadOnlySet<string> IndexedProviders();
    Task<IReadOnlyList<ProviderResult>> CallAsync(
        ProviderRequest request,
        Func<string, bool>? skip = null,
        CancellationToken cancellationToken = default
    );
    Task<(
        IReadOnlyDictionary<string, IReadOnlyDictionary<string, JsonElement>?> Structure,
        IReadOnlyList<string> Errors
    )> OverviewAsync(Func<string, bool>? skip = null, CancellationToken cancellationToken = default);
}

/// <summary>Context discovery must filter excluded adapters before any handshake or capability read.</summary>
public interface IContextProviderRegistry : IProviderRegistry
{
    bool InContext(string name);
    Task DiscoverContextAsync(CancellationToken cancellationToken = default);
    IReadOnlyList<CapabilityInfo> ContextCapabilities();
}

public sealed record ProviderProjectSnapshot(
    string Root = "",
    string ProjectId = "",
    string Branch = "",
    string Commit = "",
    string Stale = "",
    bool CrossBranch = false,
    JsonElement? Stack = null
);

/// <summary>Project metadata and optional historical observations, independent of provider adapters.</summary>
public interface IProviderProjectSession
{
    Task<ProviderProjectSnapshot> ReadProviderProjectAsync(CancellationToken cancellationToken);
    Task<IReadOnlyDictionary<string, JsonElement>> ReadProviderStatisticsAsync(
        string provider,
        CancellationToken cancellationToken
    );
}
