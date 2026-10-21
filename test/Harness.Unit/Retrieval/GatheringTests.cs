using System.Diagnostics.CodeAnalysis;
using System.Diagnostics;
using AwesomeAssertions;
using Harness.Core.Memory;
using Harness.Core.Providers;
using Harness.Core.Retrieval;
using Harness.Providers.Registry;
using Xunit;

namespace Harness.Unit.Retrieval;

[SuppressMessage("Reliability", "CA2000:Dispose objects before losing scope", Justification = "Each fixture provider transfers ownership to the asynchronously disposed registry.")]
public sealed class GatheringTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task ExpandsDefinedSymbolsQualifiersVariantsAndRelations()
    {
        await using var registry = new ProviderRegistry();
        var provider = new Fake("code", request => request.Capability is Capabilities.SymbolSearch ? request.Query switch
        {
            "OrderService.Copy" => [new() { Name = "Copy", QualifiedName = "OrderService.Copy", Path = "src/OrderService.cs", StartLine = 1 }, new() { Name = "Copy", QualifiedName = "Other.Copy", Path = "src/Other.cs", StartLine = 1 }],
            "order_template" => [],
            "orderTemplate" => [new() { Name = "orderTemplate", Path = "src/template.cs", StartLine = 1 }],
            _ => [],
        } : []);
        registry.Register(provider);
        var gathered = await RetrievalGatherer.GatherAsync(new(registry), new("fix OrderService.Copy and order_template", "src", 0), Token);
        gathered.Candidates.Single(candidate => candidate.QualifiedName is "OrderService.Copy").Scores["name"].Should().Be(1);
        gathered.Candidates.Single(candidate => candidate.QualifiedName is "Other.Copy").Scores["name"].Should().Be(0.3);
        provider.Requests.Where(request => request.Capability is Capabilities.Callers).Select(request => request.Query).Should().Equal("OrderService.Copy", "orderTemplate");
        provider.Requests.Should().OnlyContain(request => request.Path == "src" && request.Limit == 10);
    }

    [Fact]
    public async Task ExcludedProvidersAreNeverDiscoveredInspectedOrExecuted()
    {
        await using var registry = new ProviderRegistry();
        var excluded = new Fake("heavy", _ => throw new InvalidOperationException("excluded execution")) { RejectMetadata = true };
        registry.Register(excluded);
        registry.Register(new Fake("light", _ => []));
        registry.ExcludeFromContext("heavy");
        var gathered = await RetrievalGatherer.GatherAsync(new(registry), new("fix OrderService"), Token);
        gathered.Errors.Should().BeEmpty();
        excluded.Discoveries.Should().Be(0);
        excluded.CapabilityReads.Should().Be(0);
        excluded.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task FailedAndUnindexedProvidersAreSkippedWhileHealthyResultsRemain()
    {
        await using var registry = new ProviderRegistry();
        var broken = new Fake("broken", _ => throw new IOException("offline"));
        var unindexed = new Fake("unindexed", _ => throw new ProviderNotIndexedException("no index"));
        registry.Register(broken);
        registry.Register(unindexed);
        registry.Register(new Fake("healthy", request => request.Capability is Capabilities.SymbolSearch ? [new() { Name = "OrderService", Path = "src/OrderService.cs" }] : []));
        var gathered = await RetrievalGatherer.GatherAsync(new(registry), new("fix OrderService"), Token);
        gathered.Candidates.Should().NotBeEmpty();
        broken.Requests.Should().ContainSingle();
        unindexed.Requests.Should().ContainSingle();
        gathered.Errors.Should().ContainSingle().Which.Should().Contain("offline");
        gathered.Notes.Should().ContainSingle().Which.Should().Contain("no index");
    }

    [Fact]
    public async Task SharedDeadlineRetainsFastPeersAndContinuesMemoryAndGit()
    {
        await using var registry = new ProviderRegistry(TimeSpan.FromSeconds(20));
        var release = new TaskCompletionSource<ProviderResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var slow = new Fake("slow", _ => []) { Execute = (_, _) => release.Task };
        registry.Register(new Fake("fast", _ => [new() { Name = "OrderService", Path = "src/A.cs" }]));
        registry.Register(slow);
        var memoryCalls = 0;
        var gitCalls = 0;
        var watch = Stopwatch.StartNew();
        try
        {
            var gathered = await RetrievalGatherer.GatherAsync(new(registry,
                (_, _) => { memoryCalls++; return Task.FromResult(new RetrievalMemories([])); },
                (_, _, _) => { gitCalls++; return Task.FromResult<IReadOnlyList<RetrievalCommit>>([]); }), new("fix OrderService", CodeTimeout: TimeSpan.FromMilliseconds(150)), Token);
            watch.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(3));
            gathered.Candidates.Should().ContainSingle().Which.Name.Should().Be("OrderService");
            gathered.Notes.Should().ContainSingle().Which.Should().Contain("stopped after 150ms");
            gathered.Errors.Should().BeEmpty();
            memoryCalls.Should().Be(3);
            gitCalls.Should().Be(1);
        }
        finally { release.TrySetException(new IOException("late fault must be observed")); }
    }

    [Fact]
    public async Task MemoryAndRecentChangesUseBestProviderPositionsAndPreservePartialResults()
    {
        await using var registry = new ProviderRegistry();
        registry.Register(new Fake("code", request => request.Query is "fix OrderService" && request.Capability is Capabilities.SymbolSearch
            ? Enumerable.Range(0, 12).Select(i => new ProviderItem { Name = "Other", Path = $"src/{i}.cs" }).ToArray() : []));
        var queries = new List<MemoryQuery>();
        var common = Memory("m1", "decision", new("project", ""));
        var gathered = await RetrievalGatherer.GatherAsync(new(registry, (query, _) =>
        {
            queries.Add(query);
            var memory = query.Types.Contains("goal", StringComparer.Ordinal) ? Memory("goal", "goal", new("project", "")) : common;
            return Task.FromResult(new RetrievalMemories([memory]) { Errors = ["one layer offline"] });
        }, (paths, limit, _) =>
        {
            paths.Should().Equal("src/0.cs", "src/1.cs", "src/2.cs");
            limit.Should().Be(5);
            return Task.FromResult<IReadOnlyList<RetrievalCommit>>([new("0123456789abcdef", "recent", MemoryTimestamp.Parse("2026-01-02T03:04:05Z"), [paths[0]])]);
        }, "main"), new("fix OrderService"), Token);
        queries.Should().HaveCount(12);
        queries.Take(10).Should().OnlyContain(query => query.Branch == "main" && query.Limit == 20 && query.Scope!.Kind == "path");
        queries[^2].Text.Should().Be("orderservice");
        queries[^2].Branch.Should().BeEmpty();
        queries[^1].Types.Should().Equal("goal");
        var memoryCandidate = gathered.Candidates.Single(candidate => candidate.Id is "memory:m1");
        memoryCandidate.Scores["scope"].Should().Be(0.6);
        memoryCandidate.Provenance[0].Tool.Should().Be("memory (applies to src/0.cs)");
        gathered.Candidates.Single(candidate => candidate.Source is "git").Scores["recency"].Should().Be(1);
        gathered.Errors.Should().Equal("memory: one layer offline");
    }

    [Fact]
    public async Task BroadPhraseHitsLoseBoostAndHeadNounFallbackFindsFiles()
    {
        await using var registry = new ProviderRegistry();
        registry.Register(new Fake("code", request => (request.Capability, request.Query) switch
        {
            (Capabilities.SymbolSearch, "OrderTemplate") => Enumerable.Range(0, 4).Select(i => new ProviderItem { Name = "OrderTemplate", Path = $"test/{i}.cs" }).ToArray(),
            (Capabilities.FileSearch, "template") => [new() { Path = "src/template.json" }],
            _ => [],
        }));
        var gathered = await RetrievalGatherer.GatherAsync(new(registry), new("order templates"), Token);
        gathered.Candidates.Take(4).Should().OnlyContain(candidate => !candidate.Scores.ContainsKey("phrase"));
        foreach (var candidate in gathered.Candidates.Take(4))
            candidate.Scores["test"].Should().Be(1);
        gathered.Candidates[^1].Path.Should().Be("src/template.json");
        gathered.Candidates[^1].Scores["phrase"].Should().Be(1);
    }

    [Fact]
    public async Task EmptySourcesAndCallerCancellationAreExplicit()
    {
        var empty = await RetrievalGatherer.GatherAsync(new(), new(""), Token);
        empty.Candidates.Should().BeEmpty();
        empty.Errors.Should().BeEmpty();
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();
        await FluentActions.Awaiting(() => RetrievalGatherer.GatherAsync(new(), new("anything"), cancellation.Token)).Should().ThrowAsync<OperationCanceledException>();
    }

    private static MemoryRecord Memory(string id, string type, MemoryScope scope) => new(id, type, "a durable statement", scope, new(), null, "", "", null, null,
        MemoryTimestamp.Parse("2026-01-01T00:00:00Z"), MemoryTimestamp.Parse("2026-01-01T00:00:00Z"));

    [Fact]
    public async Task StalledMetadataIsBoundedAndDoesNotPreventMemoryGathering()
    {
        await using var registry = new ProviderRegistry();
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        registry.Register(new Fake("metadata", _ => []) { Discover = _ => release.Task });
        var memoryCalls = 0;
        try
        {
            var gathered = await RetrievalGatherer.GatherAsync(new(registry, (_, _) =>
            {
                memoryCalls++;
                return Task.FromResult(new RetrievalMemories([]));
            }), new("fix OrderService", CodeTimeout: TimeSpan.FromMilliseconds(100)), Token).WaitAsync(TimeSpan.FromSeconds(3), Token);
            gathered.Notes.Should().ContainSingle().Which.Should().Contain("stopped after 100ms");
            memoryCalls.Should().Be(2);
        }
        finally { release.TrySetException(new IOException("late discovery failure")); }
    }

    [Fact]
    public async Task SourceLocalCancellationIsPartialButCallerCancellationPropagates()
    {
        var gathered = await RetrievalGatherer.GatherAsync(new(Memory: (_, _) => throw new OperationCanceledException("memory deadline")), new("inspect operations"), Token);
        gathered.Errors.Should().Equal("memory: memory deadline");
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(Token);
        await FluentActions.Awaiting(() => RetrievalGatherer.GatherAsync(new(Memory: async (_, token) =>
        {
            await cancellation.CancelAsync();
            token.ThrowIfCancellationRequested();
            return new RetrievalMemories([]);
        }), new("inspect operations"), cancellation.Token)).Should().ThrowAsync<OperationCanceledException>();
        using var ignored = CancellationTokenSource.CreateLinkedTokenSource(Token);
        await FluentActions.Awaiting(() => RetrievalGatherer.GatherAsync(new(RecentChanges: async (_, _, _) =>
        {
            await ignored.CancelAsync();
            return Array.Empty<RetrievalCommit>();
        }), new(""), ignored.Token)).Should().ThrowAsync<OperationCanceledException>();
    }

    internal sealed class Fake(string name, Func<ProviderRequest, IReadOnlyList<ProviderItem>> answer) : IProvider, IProviderDiscovery
    {
        public string Name => name;
        public bool RejectMetadata { get; init; }
        public int Discoveries { get; private set; }
        public int CapabilityReads { get; private set; }
        public List<ProviderRequest> Requests { get; } = [];
        public Func<ProviderRequest, CancellationToken, Task<ProviderResult>>? Execute { get; init; }
        public Func<CancellationToken, Task>? Discover { get; init; }
        public Task DiscoverAsync(CancellationToken cancellationToken = default)
        {
            Discoveries++;
            if (RejectMetadata)
                throw new InvalidOperationException("excluded discovery");
            return Discover?.Invoke(cancellationToken) ?? Task.CompletedTask;
        }
        public IReadOnlyList<CapabilityInfo> Capabilities()
        {
            CapabilityReads++;
            if (RejectMetadata)
                throw new InvalidOperationException("excluded capabilities");
            return new[] { Core.Providers.Capabilities.SymbolSearch, Core.Providers.Capabilities.Context, Core.Providers.Capabilities.Callers, Core.Providers.Capabilities.Impact, Core.Providers.Capabilities.FileSearch }.Select(capability => new CapabilityInfo(capability, name)).ToArray();
        }
        public Task<ProviderResult> ExecuteAsync(ProviderRequest request, CancellationToken cancellationToken = default)
        {
            Requests.Add(request);
            return Execute?.Invoke(request, cancellationToken) ?? Task.FromResult(new ProviderResult { Items = answer(request).Select(item => item with { Provider = name, Tool = "fixture" }).ToArray() });
        }
        public ValueTask<ProviderHealth> HealthAsync(bool probe, CancellationToken cancellationToken = default) => ValueTask.FromResult(new ProviderHealth("ready"));
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
