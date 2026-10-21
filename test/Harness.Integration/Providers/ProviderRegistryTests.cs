using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using AwesomeAssertions;
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
            request.Query.Should().Be("needle");
            request.Path.Should().Be("src");
            request.Limit.Should().Be(2);
            request.Budget.Should().Be(800);
            request.Target.Should().Be("destination");
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
        results.Select(result => result.Provider).Should().Equal("z", "a");
        observed.Should().Equal(results);
        results.Should().AllSatisfy(result =>
        {
            result.Error.Should().BeNull();
            result.Capability.Should().Be(Capabilities.TextSearch);
            result.Truncated.Should().BeTrue();
            result.Bytes.Should().Be(73);
            result.Items[0].Provider.Should().Be("origin");
            result.Items[0].Tool.Should().Be("search");
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
        results[0].Error.Should().BeNull();
        results[1].Error.Should().BeSameAs(hint);
        results[1].Error!.Message.Should().Be("failure (lookup): boom — try y");
        results[2].Error.Should().BeOfType<ProviderNotIndexedException>();
        results[2].Items[0].Name.Should().Be("kept");
        results.Skip(3).Should().AllSatisfy(result => { result.TimedOut.Should().BeTrue(); result.Error.Should().NotBeNull(); });
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
        var result = (await call.WaitAsync(TimeSpan.FromSeconds(5), Token)).Should().ContainSingle().Which;
        result.TimedOut.Should().BeFalse();
        result.Error.Should().BeOfType<ProviderException>();
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
        registry.InContext("z").Should().BeFalse();
        registry.InContext("a").Should().BeTrue();
        registry.Capabilities().Select(info => info.Capability + "/" + info.Provider).Should().Equal("code.symbol_search/z", "code.text_search/a", "code.text_search/z");
        registry.IndexedProviders().Should().Equal("z");
        var results = await registry.CallAsync(new(Capabilities.TextSearch), name => !registry.InContext(name), Token);
        results.Should().ContainSingle().Which.Provider.Should().Be("a");
        z.Calls.Should().Be(0);
        a.Probes.Should().Be(0);
        await registry.CallAsync(new(Capabilities.TextSearch), cancellationToken: Token);
        z.Calls.Should().Be(1);
    }

    [Fact]
    public async Task EmptyRegistryAndUnsupportedCapabilitiesDoNoWork()
    {
        await using var registry = new ProviderRegistry();
        (await registry.CallAsync(new(Capabilities.FileSearch), cancellationToken: Token)).Should().BeEmpty();
        registry.Capabilities().Should().BeEmpty();
        var provider = new Fake("a", [Capabilities.TextSearch]);
        registry.Register(provider);
        (await registry.CallAsync(new(Capabilities.Flow), cancellationToken: Token)).Should().BeEmpty();
        provider.Calls.Should().Be(0);
        provider.Probes.Should().Be(0);
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
        registry.SetObserver((result, _) => { result.Items.Count.Should().Be(2); result.Bytes.Should().Be(100); });
        var result = (await registry.CallAsync(new(Capabilities.TextSearch), cancellationToken: Token)).Should().ContainSingle().Which;
        result.Items.Select(item => item.Path).Should().Equal("src/a.cs", null);
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
        structure["a"]!["name"].GetString().Should().Be("project");
        errors.Should().Equal("error: first");
        skipped.Calls.Should().Be(1);
    }

    [Fact]
    public async Task WarmIsExplicitAndCloseAttemptsEveryProvider()
    {
        var registry = new ProviderRegistry();
        var a = new Fake("a", [Capabilities.TextSearch]) { CloseError = new IOException("first") };
        var b = new Fake("b", []) { CloseError = new IOException("second") };
        registry.Register(a);
        registry.Register(b);
        a.Discoveries.Should().Be(0);
        await registry.WarmAsync();
        a.Discoveries.Should().Be(1);
        b.Discoveries.Should().Be(1);
        var error = (await FluentActions.Awaiting(() => registry.DisposeAsync().AsTask()).Should().ThrowExactlyAsync<IOException>()).Which;
        error.Message.Should().Be("first");
        a.Closes.Should().Be(1);
        b.Closes.Should().Be(1);
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
