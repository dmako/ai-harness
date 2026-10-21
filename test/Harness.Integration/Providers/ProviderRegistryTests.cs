using System.Text.Json;
using System.Diagnostics.CodeAnalysis;
using Harness.Core.Providers;
using Harness.Providers.Registry;
using Xunit;

namespace Harness.Integration.Providers;

[SuppressMessage("Reliability", "CA2000:Dispose objects before losing scope", Justification = "Register transfers ownership to the registry; its all-provider disposal, including failure paths, is tested explicitly.")]
public sealed class ProviderRegistryTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task FanoutRunsConcurrentlyAndMergesInRegistrationOrder()
    {
        await using var registry = new ProviderRegistry(TimeSpan.FromSeconds(5));
        var bothStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var count = 0;
        Task<ProviderResult> Execute(ProviderRequest request, CancellationToken token)
        {
            Assert.Equal("needle", request.Query);
            Assert.Equal("src", request.Path);
            Assert.Equal(2, request.Limit);
            Assert.Equal(800, request.Budget);
            Assert.Equal("destination", request.Target);
            return RunAsync();
            async Task<ProviderResult> RunAsync()
            {
                if (Interlocked.Increment(ref count) is 2)
                {
                    bothStarted.SetResult();
                }
                await bothStarted.Task.WaitAsync(token);
                return new() { Items = [new() { Name = "partial", Provider = "origin", Tool = "search" }], Bytes = 73, Truncated = true };
            }
        }
        registry.Register(new Fake("z", [Capabilities.TextSearch], Execute));
        registry.Register(new Fake("a", [Capabilities.TextSearch], Execute));
        registry.Register(new Fake("other", [Capabilities.Overview]));
        var observed = new List<ProviderResult>();
        registry.SetObserver((result, _) => observed.Add(result));
        var results = await registry.CallAsync(new(Capabilities.TextSearch, "needle", "src", 2, "destination", 800), cancellationToken: Token);
        Assert.Equal(["z", "a"], results.Select(result => result.Provider));
        Assert.Equal(results, observed);
        Assert.All(results, result =>
        {
            Assert.Null(result.Error);
            Assert.Equal(Capabilities.TextSearch, result.Capability);
            Assert.True(result.Truncated);
            Assert.Equal(73, result.Bytes);
            Assert.Equal("origin", result.Items[0].Provider);
            Assert.Equal("search", result.Items[0].Tool);
        });
    }

    [Fact]
    public async Task FaultsTimeoutsAndPartialResultsDoNotAffectHealthyProviders()
    {
        await using var registry = new ProviderRegistry(TimeSpan.FromMilliseconds(100));
        var hint = new ProviderException("failure", "boom", "lookup", "try y");
        registry.Register(new Fake("healthy", [Capabilities.TextSearch]));
        registry.Register(new Fake("failure", [Capabilities.TextSearch], (_, _) => throw hint));
        registry.Register(new Fake("partial", [Capabilities.TextSearch], (_, _) => Task.FromResult(new ProviderResult { Items = [new() { Name = "kept" }], Error = new ProviderNotIndexedException() })));
        registry.Register(new Fake("cancel", [Capabilities.TextSearch], async (_, token) => { await Task.Delay(Timeout.InfiniteTimeSpan, token); return new(); }));
        var release = new TaskCompletionSource<ProviderResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        registry.Register(new Fake("ignores", [Capabilities.TextSearch], (_, _) => release.Task));
        var results = await registry.CallAsync(new(Capabilities.TextSearch), cancellationToken: Token).WaitAsync(TimeSpan.FromSeconds(5), Token);
        release.SetException(new InvalidOperationException("late fault is observed"));
        Assert.Null(results[0].Error);
        Assert.Same(hint, results[1].Error);
        Assert.Equal("failure (lookup): boom — try y", results[1].Error!.Message);
        Assert.IsType<ProviderNotIndexedException>(results[2].Error);
        Assert.Equal("kept", results[2].Items[0].Name);
        Assert.All(results.Skip(3), result => { Assert.True(result.TimedOut); Assert.NotNull(result.Error); });
    }

    [Fact]
    public async Task CallerCancellationReturnsIsolatedErrorsWithoutReportingTimeout()
    {
        await using var registry = new ProviderRegistry(TimeSpan.FromSeconds(20));
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        registry.Register(new Fake("a", [Capabilities.Context], async (_, token) => { started.SetResult(); await Task.Delay(Timeout.InfiniteTimeSpan, token); return new(); }));
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(Token);
        var call = registry.CallAsync(new(Capabilities.Context), cancellationToken: cancellation.Token);
        await started.Task.WaitAsync(Token);
        await cancellation.CancelAsync();
        var result = Assert.Single(await call.WaitAsync(TimeSpan.FromSeconds(5), Token));
        Assert.False(result.TimedOut);
        Assert.IsType<ProviderException>(result.Error);
    }

    [Fact]
    public async Task MetadataAndSkipFiltersKeepOrderAndDoNotProbeHealth()
    {
        await using var registry = new ProviderRegistry();
        var z = new Fake("z", [Capabilities.TextSearch, Capabilities.SymbolSearch], freshness: "indexed");
        var a = new Fake("a", [Capabilities.TextSearch]);
        registry.Register(z);
        registry.Register(a);
        registry.ExcludeFromContext("z");
        Assert.False(registry.InContext("z"));
        Assert.True(registry.InContext("a"));
        Assert.Equal(["code.symbol_search/z", "code.text_search/a", "code.text_search/z"], registry.Capabilities().Select(info => info.Capability + "/" + info.Provider));
        Assert.Equal(["z"], registry.IndexedProviders());
        var results = await registry.CallAsync(new(Capabilities.TextSearch), name => !registry.InContext(name), Token);
        Assert.Equal("a", Assert.Single(results).Provider);
        Assert.Equal(0, z.Calls);
        Assert.Equal(0, a.Probes);
        await registry.CallAsync(new(Capabilities.TextSearch), cancellationToken: Token);
        Assert.Equal(1, z.Calls);
    }

    [Fact]
    public async Task EmptyRegistryAndUnsupportedCapabilitiesDoNoWork()
    {
        await using var registry = new ProviderRegistry();
        Assert.Empty(await registry.CallAsync(new(Capabilities.FileSearch), cancellationToken: Token));
        Assert.Empty(registry.Capabilities());
        var provider = new Fake("a", [Capabilities.TextSearch]);
        registry.Register(provider);
        Assert.Empty(await registry.CallAsync(new(Capabilities.Flow), cancellationToken: Token));
        Assert.Equal(0, provider.Calls);
        Assert.Equal(0, provider.Probes);
    }

    [Fact]
    public async Task PathFilterRunsBeforeObserverAndPreservesPathlessItems()
    {
        await using var registry = new ProviderRegistry();
        registry.Register(new Fake("a", [Capabilities.TextSearch], (_, _) => Task.FromResult(new ProviderResult
        {
            Items = [new() { Path = "src/a.cs" }, new() { Path = ".claude/worktrees/x/src/a.cs" }, new() { Name = "pathless" }],
            Bytes = 100,
        })));
        registry.SetPathFilter(path => path.StartsWith(".claude/worktrees/", StringComparison.Ordinal));
        registry.SetObserver((result, _) => { Assert.Equal(2, result.Items.Count); Assert.Equal(100, result.Bytes); });
        var result = Assert.Single(await registry.CallAsync(new(Capabilities.TextSearch), cancellationToken: Token));
        Assert.Equal(["src/a.cs", null], result.Items.Select(item => item.Path));
    }

    [Fact]
    public async Task OverviewKeepsStructuredAnswersAndOnlyFirstErrorLine()
    {
        await using var registry = new ProviderRegistry();
        registry.Register(new Fake("a", [Capabilities.Overview], (_, _) => Task.FromResult(new ProviderResult { Summary = new Dictionary<string, JsonElement> { ["name"] = JsonSerializer.SerializeToElement("project") } })));
        registry.Register(new Fake("error", [Capabilities.Overview], (_, _) => throw new ProviderException("error", "first\nsecond")));
        var skipped = new Fake("skipped", [Capabilities.Overview]);
        registry.Register(skipped);
        var (structure, errors) = await registry.OverviewAsync(name => name == "skipped", Token);
        Assert.Equal("project", structure["a"]!["name"].GetString());
        Assert.Equal(["error: first"], errors);
        Assert.Equal(1, skipped.Calls);
    }

    [Fact]
    public async Task WarmIsExplicitAndCloseAttemptsEveryProvider()
    {
        var registry = new ProviderRegistry();
        var a = new Fake("a", [Capabilities.TextSearch]) { CloseError = new IOException("first") };
        var b = new Fake("b", []) { CloseError = new IOException("second") };
        registry.Register(a);
        registry.Register(b);
        Assert.Equal(0, a.Discoveries);
        await registry.WarmAsync();
        Assert.Equal(1, a.Discoveries);
        Assert.Equal(1, b.Discoveries);
        var error = await Assert.ThrowsAsync<IOException>(() => registry.DisposeAsync().AsTask());
        Assert.Equal("first", error.Message);
        Assert.Equal(1, a.Closes);
        Assert.Equal(1, b.Closes);
    }

    internal class Fake(string name, string[] capabilities, Func<ProviderRequest, CancellationToken, Task<ProviderResult>>? execute = null, string freshness = "live", string latency = "low", string health = "ready") : IProvider, IVersionedProvider
    {
        private int _calls;
        private int _discoveries;
        private int _probes;
        public string Name => name;
        public string ServerVersion => "fixture-1";
        public int Calls => _calls;
        public int Discoveries => _discoveries;
        public int Probes => _probes;
        public int Closes { get; private set; }
        public Exception? CloseError { get; init; }
        public IReadOnlyList<CapabilityInfo> Capabilities()
        {
            Interlocked.Increment(ref _discoveries);
            return capabilities.Select(capability => new CapabilityInfo(capability, name, "low", latency, freshness, "project")).ToArray();
        }
        public ValueTask<ProviderHealth> HealthAsync(bool probe, CancellationToken cancellationToken = default)
        {
            if (probe)
            {
                Interlocked.Increment(ref _probes);
            }
            return ValueTask.FromResult(new ProviderHealth(health));
        }
        public Task<ProviderResult> ExecuteAsync(ProviderRequest request, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _calls);
            return execute?.Invoke(request, cancellationToken) ?? Task.FromResult(new ProviderResult());
        }
        public ValueTask DisposeAsync()
        {
            Closes++;
            return CloseError is null ? ValueTask.CompletedTask : ValueTask.FromException(CloseError);
        }
    }
}
