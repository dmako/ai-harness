using System.Diagnostics;
using Harness.Core.Providers;

namespace Harness.Providers.Registry;

/// <summary>Routes calls independently and keeps provider registration order in merged answers.</summary>
public sealed class ProviderRegistry : IContextProviderRegistry, IAsyncDisposable
{
    private readonly object _gate = new();
    private readonly List<IProvider> _providers = [];
    private readonly HashSet<string> _noContext = new(StringComparer.Ordinal);
    private readonly TimeSpan _timeout;
    private Action<ProviderResult, ProviderRequest>? _observer;
    private Func<string, bool>? _skipPath;

    public ProviderRegistry(TimeSpan timeout = default) => _timeout = timeout > TimeSpan.Zero ? timeout : TimeSpan.FromSeconds(30);

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

    public Task DiscoverAsync(CancellationToken cancellationToken = default) => Task.WhenAll(Providers().OfType<IProviderDiscovery>().Select(provider => provider.DiscoverAsync(cancellationToken)));

    public Task DiscoverContextAsync(CancellationToken cancellationToken = default) => Task.WhenAll(Providers().Where(provider => InContext(provider.Name)).OfType<IProviderDiscovery>().Select(provider => provider.DiscoverAsync(cancellationToken)));
    public IReadOnlyList<CapabilityInfo> ContextCapabilities() => Providers().Where(provider => InContext(provider.Name)).SelectMany(provider => provider.Capabilities())
        .OrderBy(info => info.Capability, StringComparer.Ordinal).ThenBy(info => info.Provider, StringComparer.Ordinal).ToArray();

    public IReadOnlyList<CapabilityInfo> Capabilities() => Providers().SelectMany(provider => provider.Capabilities())
        .OrderBy(info => info.Capability, StringComparer.Ordinal).ThenBy(info => info.Provider, StringComparer.Ordinal).ToArray();

    public IReadOnlyList<IProvider> For(string capability) => Providers()
        .Where(provider => provider.Capabilities().Any(info => info.Capability == capability)).ToArray();

    public IReadOnlySet<string> IndexedProviders() => Capabilities().Where(info => info.Freshness == "indexed")
        .Select(info => info.Provider).ToHashSet(StringComparer.Ordinal);

    public async Task<IReadOnlyList<ProviderResult>> CallAsync(ProviderRequest request, Func<string, bool>? skip = null, CancellationToken cancellationToken = default)
    {
        var included = Providers().Where(provider => skip is null || !skip(provider.Name)).ToArray();
        await Task.WhenAll(included.OfType<IProviderDiscovery>().Select(provider => provider.DiscoverAsync(cancellationToken))).ConfigureAwait(false);
        var providers = included.Where(provider => provider.Capabilities().Any(info => info.Capability == request.Capability));
        // Task.Run also isolates adapters doing synchronous work before their first await.
        var results = await Task.WhenAll(providers.Select(provider => CallOneAsync(provider, request, cancellationToken))).ConfigureAwait(false);
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
                results[i] = results[i] with { Items = results[i].Items.Where(item => string.IsNullOrEmpty(item.Path) || !skipPath(item.Path)).ToArray() };
            }
            observer?.Invoke(results[i], request);
        }
        return results;
    }

    public async Task<(IReadOnlyDictionary<string, IReadOnlyDictionary<string, System.Text.Json.JsonElement>?> Structure, IReadOnlyList<string> Errors)> OverviewAsync(
        Func<string, bool>? skip = null, CancellationToken cancellationToken = default)
    {
        var structure = new Dictionary<string, IReadOnlyDictionary<string, System.Text.Json.JsonElement>?>(StringComparer.Ordinal);
        var errors = new List<string>();
        // Preserve Go's overview skip semantics: skip controls inclusion, not execution.
        foreach (var result in await CallAsync(new(Core.Providers.Capabilities.Overview), cancellationToken: cancellationToken).ConfigureAwait(false))
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

    private async Task<ProviderResult> CallOneAsync(IProvider provider, ProviderRequest request, CancellationToken cancellationToken)
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
            _ = execution.ContinueWith(task => _ = task.Exception, CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            result = await execution.WaitAsync(linked.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException error) when (linked.IsCancellationRequested)
        {
            result = new() { Error = new ProviderException(provider.Name, deadline.IsCancellationRequested ? $"timed out after {_timeout.TotalSeconds:g}s" : "operation cancelled", innerException: error) };
        }
        catch (Exception error)
        {
            result = new() { Error = error };
        }
        return result with { Provider = provider.Name, Capability = request.Capability, Latency = Stopwatch.GetElapsedTime(start), TimedOut = result.TimedOut || deadline.IsCancellationRequested };
    }
}
