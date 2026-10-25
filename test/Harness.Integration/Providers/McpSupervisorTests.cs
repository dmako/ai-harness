using System.Diagnostics.CodeAnalysis;
using AwesomeAssertions;
using Harness.Core.Providers;
using Harness.Infrastructure.Processes;
using Harness.Providers.Mcp;
using Harness.Providers.Registry;
using Xunit;

namespace Harness.Integration.Providers;

[Collection("Provider processes")]
public sealed class McpSupervisorTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task ConcurrentFirstUseCreatesExactlyOneReusableSession()
    {
        using var fixture = new ProviderFixture();
        var factory = new CountingFactory();
        await using var supervisor = new McpSupervisor(factory, fixture.Options());
        supervisor.Health().State.Should().Be("idle");
        factory.Ids.Should().BeEmpty();
        var clients = await Task.WhenAll(Enumerable.Range(0, 12).Select(_ => supervisor.ClientAsync(Token)));
        clients.Select(client => client.Id).Distinct().Should().ContainSingle();
        factory.Ids.Should().ContainSingle();
        supervisor.Health().State.Should().Be("ready");
        supervisor.ServerVersion.Should().Be("provider-fixture 1.0");
        await Task.WhenAll(
            Enumerable
                .Range(0, 6)
                .Select(_ => supervisor.CallToolAsync("echo", new Dictionary<string, object?>(), Token))
        );
        await Task.WhenAll(CloseAsync(), CloseAsync());
        supervisor.Health().Detail.Should().Be("closed");
        supervisor.ServerVersion.Should().BeEmpty();
        await FluentActions
            .Awaiting(() => supervisor.ClientAsync(Token))
            .Should()
            .ThrowAsync<ObjectDisposedException>();
        await ProviderFixture.GoneAsync(factory.Ids.Single());
        async Task CloseAsync() => await supervisor.DisposeAsync();
    }

    [Fact]
    public async Task CrashBackoffAndRestartAreVisibleWithoutProbing()
    {
        using var fixture = new ProviderFixture();
        var factory = new CountingFactory();
        var clock = new Clock();
        await using var supervisor = new McpSupervisor(factory, fixture.Options(), timeProvider: clock);
        var first = await supervisor.ClientAsync(Token);
        await FluentActions
            .Awaiting(() => supervisor.CallToolAsync("crash", new Dictionary<string, object?>(), Token))
            .Should()
            .ThrowAsync<IOException>();
        await ProviderFixture.EventuallyAsync(() => supervisor.Health().State is "degraded");
        await FluentActions
            .Awaiting(() => supervisor.ClientAsync(Token))
            .Should()
            .ThrowAsync<IOException>()
            .WithMessage("*restarting*");
        factory.Ids.Should().ContainSingle();
        clock.Advance(TimeSpan.FromMilliseconds(500));
        var second = await supervisor.ClientAsync(Token);
        second.Id.Should().NotBe(first.Id);
        supervisor.Health().Detail.Should().Contain("restarted 1 time(s)");
        await supervisor.DisposeAsync();
        foreach (var id in factory.Ids)
        {
            await ProviderFixture.GoneAsync(id);
        }
    }

    [Fact]
    public async Task FailureWindowAndExponentialBackoffFailFastAndRecover()
    {
        using var fixture = new ProviderFixture();
        var clock = new Clock();
        var options = fixture.Options() with { Process = new(Path.Combine(fixture.Root, "missing-provider"), []) };
        await using var supervisor = new McpSupervisor(
            new ProviderProcessFactory(),
            options,
            new()
            {
                BaseBackoff = TimeSpan.FromMilliseconds(50),
                MaxBackoff = TimeSpan.FromMilliseconds(200),
                Window = TimeSpan.FromMinutes(1),
            },
            clock
        );
        await FluentActions.Awaiting(() => supervisor.ClientAsync(Token)).Should().ThrowAsync<Exception>();
        supervisor.Health().State.Should().Be("degraded");
        await FluentActions
            .Awaiting(() => supervisor.ClientAsync(Token))
            .Should()
            .ThrowAsync<IOException>()
            .WithMessage("*restarting*");
        clock.Advance(TimeSpan.FromMilliseconds(50));
        await FluentActions.Awaiting(() => supervisor.ClientAsync(Token)).Should().ThrowAsync<Exception>();
        clock.Advance(TimeSpan.FromMilliseconds(99));
        await FluentActions
            .Awaiting(() => supervisor.ClientAsync(Token))
            .Should()
            .ThrowAsync<IOException>()
            .WithMessage("*restarting*");
        clock.Advance(TimeSpan.FromMilliseconds(1));
        await FluentActions.Awaiting(() => supervisor.ClientAsync(Token)).Should().ThrowAsync<Exception>();
        supervisor.Health().State.Should().Be("unavailable");
        await FluentActions
            .Awaiting(() => supervisor.ClientAsync(Token))
            .Should()
            .ThrowAsync<IOException>()
            .WithMessage("*failed 3 times*");
        clock.Advance(TimeSpan.FromMinutes(1));
        await FluentActions.Awaiting(() => supervisor.ClientAsync(Token)).Should().ThrowAsync<Exception>();
        supervisor.Health().State.Should().Be("degraded");
        clock.Advance(TimeSpan.FromMilliseconds(199));
        await FluentActions
            .Awaiting(() => supervisor.ClientAsync(Token))
            .Should()
            .ThrowAsync<IOException>()
            .WithMessage("*restarting*");
    }

    [Fact]
    public async Task CloseCancelsAnInFlightHandshakeAndNeverPublishesAnOrphan()
    {
        using var fixture = new ProviderFixture();
        var factory = new CountingFactory();
        await using var supervisor = new McpSupervisor(factory, fixture.Options("stall-start"));
        var starting = supervisor.ClientAsync(Token);
        await ProviderFixture.EventuallyAsync(() => factory.Ids.Count is 1);
        await supervisor.DisposeAsync();
        await FluentActions.Awaiting(() => starting).Should().ThrowAsync<OperationCanceledException>();
        await ProviderFixture.GoneAsync(factory.Ids.Single());
        supervisor.Health().Detail.Should().Be("closed");
    }

    [Fact]
    [SuppressMessage(
        "Reliability",
        "CA2000:Dispose objects before losing scope",
        Justification = "The registry takes ownership of the registered conformance provider."
    )]
    public async Task RegistryPreservesShortRpcTimeoutObservationAndHealthySibling()
    {
        using var fixture = new ProviderFixture();
        await using var registry = new ProviderRegistry(TimeSpan.FromSeconds(20));
        var configuration = new ProviderConfiguration
        {
            Type = "mcp",
            Command = "dotnet",
            Args = [ProviderFixture.Worker, "normal", fixture.Root],
            TimeoutMs = 150,
            Capabilities = new(StringComparer.Ordinal) { [Capabilities.TextSearch] = new() { Tool = "hang" } },
        };
        registry.Register(
            GenericMcpProvider.Build("stalled", configuration, fixture.Root, new ProviderProcessFactory())
        );
        registry.Register(new ProviderRegistryTests.Fake("healthy", [Capabilities.TextSearch]));
        var results = await registry.CallAsync(new(Capabilities.TextSearch), cancellationToken: Token);
        results[0].TimedOut.Should().BeTrue();
        results[0]
            .Error.Should()
            .BeOfType<ProviderException>()
            .Which.InnerException.Should()
            .BeOfType<TimeoutException>();
        results[1].Error.Should().BeNull();
    }

    private sealed class Clock : TimeProvider
    {
        private readonly object _gate = new();
        private DateTimeOffset _now = DateTimeOffset.UtcNow;

        public override DateTimeOffset GetUtcNow()
        {
            lock (_gate)
            {
                return _now;
            }
        }

        public void Advance(TimeSpan time)
        {
            lock (_gate)
            {
                _now += time;
            }
        }
    }
}
