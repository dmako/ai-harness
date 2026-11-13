using System.Diagnostics;
using Harness.Core.Providers;

namespace Harness.Providers.Registry;

/// <summary>Routes calls independently and keeps provider registration order in merged answers.</summary>
public sealed class ProviderRegistry : IContextProviderRegistry, IAsyncDisposable
{
    private readonly object _gate = new();
    private readonly List<IProvider> _providers = [];
    private readonly HashSet<string> _noContext = new(StringComparer.Ordinal);
    private readonly List<UnavailableProvider> _unavailable = [];
    private readonly TimeSpan _timeout;
    private Action<ProviderResult, ProviderRequest>? _observer;
    private Func<string, bool>? _skipPath;

    public ProviderRegistry(TimeSpan timeout = default) =>
        _timeout = timeout > TimeSpan.Zero ? timeout : TimeSpan.FromSeconds(30);

    public void Register(IProvider provider)
    {
        ArgumentNullException.ThrowIfNull(provider);
        lock (_gate)
        {
            _providers.Add(provider);
        }
    }

    public IReadOnlyList<IProvider> Providers()
    {
        lock (_gate)
        {
            return _providers.ToArray();
        }
    }

    /// <summary>Records a provider that could not be registered, for provider_status to name.</summary>
    public void AddUnavailable(UnavailableProvider provider)
    {
        ArgumentNullException.ThrowIfNull(provider);
        lock (_gate)
        {
            _unavailable.Add(provider);
        }
    }

    public IReadOnlyList<UnavailableProvider> Unavailable()
    {
        lock (_gate)
        {
            return _unavailable.ToArray();
        }
    }

    public void SetObserver(Action<ProviderResult, ProviderRequest>? observer)
    {
        lock (_gate)
        {
            _observer = observer;
        }
    }

    public void SetPathFilter(Func<string, bool>? skipPath)
    {
        lock (_gate)
        {
            _skipPath = skipPath;
        }
    }

    public void ExcludeFromContext(string name)
    {
        lock (_gate)
        {
            _noContext.Add(name);
        }
    }

    public bool InContext(string name)
    {
        lock (_gate)
        {
            return !_noContext.Contains(name);
        }
    }

    /// <summary>Discovers every provider side by side, each within its time budget; a stuck one holds back no other.</summary>
    public Task DiscoverAsync(CancellationToken cancellationToken = default) =>
        Task.WhenAll(Providers().Select(provider => DiscoverOneAsync(provider, cancellationToken)));

    public Task DiscoverContextAsync(CancellationToken cancellationToken = default) =>
        Task.WhenAll(
            Providers()
                .Where(provider => InContext(provider.Name))
                .Select(provider => DiscoverOneAsync(provider, cancellationToken))
        );

    public IReadOnlyList<CapabilityInfo> ContextCapabilities() =>
        Providers()
            .Where(provider => InContext(provider.Name))
            .SelectMany(provider => provider.Capabilities())
            .OrderBy(info => info.Capability, StringComparer.Ordinal)
            .ThenBy(info => info.Provider, StringComparer.Ordinal)
            .ToArray();

    public IReadOnlyList<CapabilityInfo> Capabilities() =>
        Providers()
            .SelectMany(provider => provider.Capabilities())
            .OrderBy(info => info.Capability, StringComparer.Ordinal)
            .ThenBy(info => info.Provider, StringComparer.Ordinal)
            .ToArray();

    public IReadOnlyList<IProvider> For(string capability) =>
        Providers().Where(provider => provider.Capabilities().Any(info => info.Capability == capability)).ToArray();

    public IReadOnlySet<string> IndexedProviders() =>
        Capabilities()
            .Where(info => info.Freshness == "indexed")
            .Select(info => info.Provider)
            .ToHashSet(StringComparer.Ordinal);

    public async Task<IReadOnlyList<ProviderResult>> CallAsync(
        ProviderRequest request,
        Func<string, bool>? skip = null,
        CancellationToken cancellationToken = default
    )
    {
        var included = Providers().Where(provider => skip is null || !skip(provider.Name)).ToArray();
        // each provider is discovered and asked on its own: a slow one holds back no other
        var results = (
            await Task.WhenAll(included.Select(provider => DiscoverAndCallAsync(provider, request, cancellationToken)))
                .ConfigureAwait(false)
        )
            .OfType<ProviderResult>()
            .ToArray();
        Action<ProviderResult, ProviderRequest>? observer;
        Func<string, bool>? skipPath;
        lock (_gate)
        {
            observer = _observer;
            skipPath = _skipPath;
        }
        for (var i = 0; i < results.Length; i++)
        {
            if (skipPath is not null)
            {
                results[i] = results[i] with
                {
                    Items = results[i]
                        .Items.Where(item => string.IsNullOrEmpty(item.Path) || !skipPath(item.Path))
                        .ToArray(),
                };
            }
            observer?.Invoke(results[i], request);
        }
        return results;
    }

    /// <summary>
    /// Discovers a provider within its time budget: null when done (or nothing to discover), else why it was not,
    /// a timeout or the caller's deadline.
    /// </summary>
    private async Task<ProviderException?> DiscoverOneAsync(IProvider provider, CancellationToken cancellationToken)
    {
        if (provider is not IProviderDiscovery discovery)
        {
            return null;
        }

        using var budget = new CancellationTokenSource(_timeout);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, budget.Token);
        try
        {
            await discovery.DiscoverAsync(linked.Token).ConfigureAwait(false);
            return null;
        }
        catch (OperationCanceledException error) when (linked.IsCancellationRequested)
        {
            return new ProviderException(
                provider.Name,
                budget.IsCancellationRequested
                    ? $"discovery timed out after {_timeout.TotalSeconds:g}s"
                    : "operation cancelled",
                innerException: error
            );
        }
    }

    /// <summary>
    /// Discovers a provider within its time budget, then asks it when it offers the capability (null when it does
    /// not). A discovery that does not finish in time, or that the caller's deadline cuts short, is that provider's
    /// failure, named in its result; the call itself answers with the others.
    /// </summary>
    private async Task<ProviderResult?> DiscoverAndCallAsync(
        IProvider provider,
        ProviderRequest request,
        CancellationToken cancellationToken
    )
    {
        var start = Stopwatch.GetTimestamp();
        if (await DiscoverOneAsync(provider, cancellationToken).ConfigureAwait(false) is { } failure)
        {
            return new()
            {
                Error = failure,
                Provider = provider.Name,
                Capability = request.Capability,
                Latency = Stopwatch.GetElapsedTime(start),
                TimedOut = failure.Message.Contains("timed out", StringComparison.Ordinal),
            };
        }

        // Task.Run in CallOneAsync also isolates adapters doing synchronous work before their first await.
        return provider.Capabilities().Any(info => info.Capability == request.Capability)
            ? await CallOneAsync(provider, request, cancellationToken).ConfigureAwait(false)
            : null;
    }

    public async Task<(
        IReadOnlyDictionary<string, IReadOnlyDictionary<string, System.Text.Json.JsonElement>?> Structure,
        IReadOnlyList<string> Errors
    )> OverviewAsync(Func<string, bool>? skip = null, CancellationToken cancellationToken = default)
    {
        var structure = new Dictionary<string, IReadOnlyDictionary<string, System.Text.Json.JsonElement>?>(
            StringComparer.Ordinal
        );
        var errors = new List<string>();
        // Overview skip controls inclusion, not execution.
        foreach (
            var result in await CallAsync(
                    new(Core.Providers.Capabilities.Overview),
                    cancellationToken: cancellationToken
                )
                .ConfigureAwait(false)
        )
        {
            if (skip?.Invoke(result.Provider) is true)
            {
                continue;
            }
            if (result.Error is { } error)
            {
                errors.Add(error.Message.Split('\n')[0]);
            }
            else
            {
                structure[result.Provider] = result.Summary;
            }
        }
        return (structure, errors);
    }

    /// <summary>Explicit warm-up; constructing a registry never starts providers.</summary>
    public Task WarmAsync() => Task.WhenAll(Providers().Select(provider => Task.Run(() => provider.Capabilities())));

    public async ValueTask DisposeAsync()
    {
        Exception? first = null;
        foreach (var provider in Providers())
        {
            try
            {
                await provider.DisposeAsync().ConfigureAwait(false);
            }
            catch (Exception error)
            {
                first ??= error;
            }
        }
        if (first is not null)
        {
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(first).Throw();
        }
    }

    private async Task<ProviderResult> CallOneAsync(
        IProvider provider,
        ProviderRequest request,
        CancellationToken cancellationToken
    )
    {
        using var deadline = new CancellationTokenSource(_timeout);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, deadline.Token);
        var start = Stopwatch.GetTimestamp();
        ProviderResult result;
        try
        {
            linked.Token.ThrowIfCancellationRequested();
            var token = linked.Token;
            var execution = Task.Run(() => provider.ExecuteAsync(request, token), CancellationToken.None);
            // Observe late faults if an adapter ignores cancellation. Never mutate returned results later.
            _ = execution.ContinueWith(
                task => _ = task.Exception,
                CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default
            );
            result = await execution.WaitAsync(linked.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException error) when (linked.IsCancellationRequested)
        {
            result = new()
            {
                Error = new ProviderException(
                    provider.Name,
                    deadline.IsCancellationRequested
                        ? $"timed out after {_timeout.TotalSeconds:g}s"
                        : "operation cancelled",
                    innerException: error
                ),
            };
        }
        catch (Exception error)
        {
            result = new() { Error = error };
        }
        return result with
        {
            Provider = provider.Name,
            Capability = request.Capability,
            Latency = Stopwatch.GetElapsedTime(start),
            TimedOut = result.TimedOut || deadline.IsCancellationRequested,
        };
    }
}
