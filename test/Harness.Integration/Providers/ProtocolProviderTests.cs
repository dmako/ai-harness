using System.Diagnostics.CodeAnalysis;
using System.IO.Pipelines;
using System.Text.Json;
using AwesomeAssertions;
using Harness.Core.Providers;
using Harness.Infrastructure.Processes;
using Harness.Providers.Protocol;
using Microsoft.Extensions.Logging.Abstractions;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using Xunit;

namespace Harness.Integration.Providers;

[Collection("Provider processes")]
public sealed class ProtocolProviderTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;
    internal const string Info = """
        {"protocol":"harness-provider/1.7","name":"analyzer","capabilities":[{"capability":"code.symbol_search","freshness":"indexed","cost":"low","latency":"other","evidence":"exact"},{"capability":"package.info"}],"refresh":true}
        """;
    internal const string Answer = """
        {"items":[{"kind":"symbol","name":"Alpha","path":"src/A.cs","start_line":7,"provider":"spoofed","tool":""},{"kind":"symbol","name":"Beta","provider":"spoofed","tool":"custom"}],"summary":{"count":2},"text":"žluťoučký 🧪"}
        """;

    internal static ProtocolProvider Create(
        ProviderFixture fixture,
        IProviderProcessFactory? factory = null,
        int timeout = 5000
    ) => new("analyzer", Configuration(fixture, timeout), fixture.Root, factory ?? new ProviderProcessFactory());

    internal static ProviderConfiguration Configuration(ProviderFixture fixture, int timeout = 5000) =>
        new()
        {
            Type = "harness",
            Command = "dotnet",
            Args = [ProviderFixture.Worker, "normal", fixture.Root],
            Dir = "project",
            TimeoutMs = timeout,
        };

    [Theory]
    [InlineData("harness-provider/1", true)]
    [InlineData("harness-provider/1.99", true)]
    [InlineData("harness-provider/10", false)]
    [InlineData("harness-provider/2", false)]
    [InlineData("harness-provider/0", false)]
    public void AcceptsOnlyTheV1Major(string version, bool compatible) =>
        ProviderProtocol.Compatible(version).Should().Be(compatible);

    [Fact]
    public async Task LazyHandshakeNormalizesHintsAndCallsPreserveFieldsAndProvenance()
    {
        using var fixture = new ProviderFixture();
        fixture.Write("harness_provider_info.json", Info);
        fixture.Write("code_symbol_search.json", Answer);
        fixture.Write(
            "package_info.result.json",
            """{"content":[{"type":"text","text":"wrong"}],"structuredContent":{"items":[],"text":"public API"}}"""
        );
        fixture.Write("harness_refresh.json", """{"detail":"reindexed"}""");
        var factory = new CountingFactory();
        await using var provider = Create(fixture, factory);
        (await provider.HealthAsync(false, Token)).State.Should().Be("idle");
        factory.Ids.Should().BeEmpty();
        var capabilities = provider.Capabilities();
        capabilities[0]
            .Should()
            .Be(
                new CapabilityInfo(
                    Capabilities.SymbolSearch,
                    "analyzer",
                    "low",
                    "medium",
                    "indexed",
                    "project",
                    "exact"
                )
            );
        capabilities[1].Scope.Should().Be("external");
        var result = await provider.ExecuteAsync(
            new(Capabilities.SymbolSearch, "Alpha", "src", 1, "Beta", 1024),
            Token
        );
        result.Error.Should().BeNull();
        result
            .Items.Should()
            .ContainSingle()
            .Which.Should()
            .BeEquivalentTo(
                new ProviderItem
                {
                    Kind = "symbol",
                    Name = "Alpha",
                    Path = "src/A.cs",
                    StartLine = 7,
                    Provider = "analyzer",
                    Tool = "code_symbol_search",
                }
            );
        result.Truncated.Should().BeTrue();
        result.Bytes.Should().Be(System.Text.Encoding.UTF8.GetByteCount(Answer));
        result.Summary!["count"].GetInt32().Should().Be(2);
        using var args = JsonDocument.Parse(
            await File.ReadAllTextAsync(Path.Combine(fixture.Root, "code_symbol_search.args"), Token)
        );
        args.RootElement.GetProperty("target").GetString().Should().Be("Beta");
        args.RootElement.GetProperty("budget").GetInt32().Should().Be(1024);
        (await provider.ExecuteAsync(new(Capabilities.PackageInfo, Package: "Foo", Version: "1.2"), Token))
            .Text.Should()
            .Be("public API");
        (await provider.RefreshAsync(new(true, ["src/A.cs"]), Token)).Detail.Should().Be("reindexed");
        (await provider.HealthAsync(true, Token)).Detail.Should().Contain("analyzer, protocol harness-provider/1.7");
        factory.Ids.Should().ContainSingle();
    }

    [Fact]
    public async Task RejectsAndCachesBadHandshakeButDoesNotAdvertiseCapabilities()
    {
        using var fixture = new ProviderFixture();
        fixture.Write("harness_provider_info.json", """{"protocol":"harness-provider/2","name":"future"}""");
        var clock = new Clock();
        await using var provider = new ProtocolProvider(
            "analyzer",
            Configuration(fixture),
            fixture.Root,
            new ProviderProcessFactory(),
            clock: clock
        );
        provider.Capabilities().Should().BeEmpty();
        fixture.Write("harness_provider_info.json", Info);
        var health = await provider.HealthAsync(true, Token);
        health.State.Should().Be("unavailable");
        health.Detail.Should().Contain("harness-provider/2");
        (await provider.ExecuteAsync(new(Capabilities.SymbolSearch), Token))
            .Error.Should()
            .BeOfType<ProviderException>();
        clock.Now += TimeSpan.FromSeconds(31);
        (await provider.HealthAsync(true, Token)).State.Should().Be("ready");
    }

    [Fact]
    public async Task UnsupportedRefreshToolErrorsAndMalformedResponsesAreExplicit()
    {
        using var fixture = new ProviderFixture();
        fixture.Write(
            "harness_provider_info.json",
            """{"protocol":"harness-provider/1","capabilities":[{"capability":"code.symbol_search"}]}"""
        );
        await using var provider = Create(fixture);
        (await provider.ExecuteAsync(new(Capabilities.TextSearch), Token))
            .Error.Should()
            .BeOfType<UnsupportedCapabilityException>();
        (await provider.RefreshAsync(new(), Token)).Should().Be(new RefreshResult(true, "provider keeps no index"));
        fixture.Write("code_symbol_search.json", """{"error":"corrupt index","hint":"rebuild"}""");
        fixture.Write("code_symbol_search.error", "");
        var error = (await provider.ExecuteAsync(new(Capabilities.SymbolSearch), Token))
            .Error.Should()
            .BeOfType<ProviderException>()
            .Which;
        error.Detail.Should().Be("corrupt index");
        error.Hint.Should().Be("rebuild");
        File.Delete(Path.Combine(fixture.Root, "code_symbol_search.error"));
        fixture.Write("code_symbol_search.json", "not JSON");
        (await provider.ExecuteAsync(new(Capabilities.SymbolSearch), Token))
            .Error!.Message.Should()
            .Contain("unexpected response");
    }

    [Fact]
    public async Task DiscoveryIsAsynchronousSingleFlightAndCancellationAware()
    {
        using var fixture = new ProviderFixture();
        fixture.Write("harness_provider_info.json", Info);
        fixture.Write("harness_provider_info.delay", "1000");
        var factory = new CountingFactory();
        await using var provider = Create(fixture, factory);
        var first = provider.DiscoverAsync(Token);
        first.IsCompleted.Should().BeFalse();
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(Token);
        var waiting = provider.DiscoverAsync(cancellation.Token);
        await cancellation.CancelAsync();
        await FluentActions.Awaiting(() => waiting).Should().ThrowAsync<OperationCanceledException>();
        await first;
        provider.Capabilities().Should().HaveCount(2);
        factory.Ids.Should().ContainSingle();
    }

    [Fact]
    public async Task ProtocolTimeoutKillsOnlyItsOwnSession()
    {
        using var fixture = new ProviderFixture();
        fixture.Write("harness_provider_info.json", Info);
        fixture.Write("code_symbol_search.delay", "60000");
        var factory = new CountingFactory();
        await using var provider = Create(fixture, factory, 800);
        provider.Capabilities().Should().NotBeEmpty();
        var result = await provider.ExecuteAsync(new(Capabilities.SymbolSearch), Token);
        result.TimedOut.Should().BeTrue();
        result.Error.Should().NotBeNull();
        await ProviderFixture.GoneAsync(factory.Ids.Single());
    }

    [Fact]
    [SuppressMessage(
        "Reliability",
        "CA2000:Dispose objects before losing scope",
        Justification = "ProtocolServer.RunAsync owns and closes the provider."
    )]
    [SuppressMessage(
        "Reliability",
        "CA2025:Do not pass disposable objects into unawaited tasks",
        Justification = "The server is awaited in finally before streams and lifetime are disposed."
    )]
    public async Task ProviderServerSharesTheContractAndClosesTheOwnedProvider()
    {
        var incoming = new Pipe();
        var outgoing = new Pipe();
        using var input = incoming.Reader.AsStream();
        using var output = outgoing.Writer.AsStream();
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(Token);
        var provider = new ServedProvider();
        var running = ProtocolServer.RunAsync(
            provider,
            "test",
            input,
            output,
            NullLoggerFactory.Instance,
            lifetime.Token
        );
        try
        {
            await using var client = await McpClient.CreateAsync(
                new StreamClientTransport(incoming.Writer.AsStream(), outgoing.Reader.AsStream()),
                new McpClientOptions { ProtocolVersion = "2025-11-25" },
                cancellationToken: Token
            );
            var info = await client.CallToolAsync("harness_provider_info", cancellationToken: Token);
            info.StructuredContent!.Value.GetProperty("protocol").GetString().Should().Be(ProviderProtocol.Version);
            var tools = await client.ListToolsAsync(cancellationToken: Token);
            tools.Select(tool => tool.Name).Should().Contain("harness_refresh").And.Contain("code_symbol_search");
            var result = await client.CallToolAsync(
                "code_symbol_search",
                new Dictionary<string, object?> { ["query"] = "Alpha" },
                cancellationToken: Token
            );
            result.StructuredContent!.Value.GetProperty("items").GetArrayLength().Should().Be(0);
            var invalid = await client.CallToolAsync(
                "code_symbol_search",
                new Dictionary<string, object?> { ["query"] = null },
                cancellationToken: Token
            );
            invalid.IsError.Should().BeTrue();
            (await client.CallToolAsync("harness_refresh", cancellationToken: Token))
                .StructuredContent!.Value.GetProperty("detail")
                .GetString()
                .Should()
                .Be("done");
        }
        finally
        {
            await lifetime.CancelAsync();
            try
            {
                await running.WaitAsync(TimeSpan.FromSeconds(5), Token);
            }
            catch (OperationCanceledException) when (lifetime.IsCancellationRequested)
            { /* Expected server shutdown. */
            }
        }
        provider.Disposed.Should().BeTrue();
    }

    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = DateTimeOffset.UtcNow;

        public override DateTimeOffset GetUtcNow() => Now;
    }

    private sealed class ServedProvider : IProviderRefresher
    {
        public bool Disposed { get; private set; }
        public string Name => "served";

        public IReadOnlyList<CapabilityInfo> Capabilities() => [new(Core.Providers.Capabilities.SymbolSearch, Name)];

        public ValueTask<ProviderHealth> HealthAsync(bool probe, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(new ProviderHealth("ready"));

        public Task<ProviderResult> ExecuteAsync(
            ProviderRequest request,
            CancellationToken cancellationToken = default
        ) => Task.FromResult(new ProviderResult());

        public Task<RefreshResult> RefreshAsync(
            RefreshRequest request,
            CancellationToken cancellationToken = default
        ) => Task.FromResult(new RefreshResult(Detail: "done"));

        public ValueTask DisposeAsync()
        {
            Disposed = true;
            return ValueTask.CompletedTask;
        }
    }
}
