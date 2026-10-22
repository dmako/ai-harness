using AwesomeAssertions;
using Harness.Core.Providers;
using Harness.Providers.Registry;
using Xunit;
using Fake = Harness.Integration.Providers.ProviderRegistryTests.Fake;

namespace Harness.Integration.Providers;

public sealed class ProviderRoutingTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task BuildFiltersDisabledUnknownAndLightProvidersWithoutConstructingThem()
    {
        using var root = new Root();
        root.Write("App.slnx", "");
        IProvider Forbidden() => throw new InvalidOperationException("factory must not run");
        ProviderRegistration[] entries =
        [
            new("disabled", "mcp", Forbidden, Enabled: false),
            new("unknown", "other", Forbidden),
            new("heavy", "harness", Forbidden, Context: false),
            new("z", "mcp", () => new Fake("z", [Capabilities.TextSearch])),
            new("a", "mcp", () => new Fake("a", [Capabilities.SymbolSearch])),
        ];
        await using var registry = ProviderRouting.Build(entries, root.Path, light: true);
        registry.Providers().Select(provider => provider.Name).Should().Equal("a", "z");
        registry
            .Providers()
            .Should()
            .AllSatisfy(provider => provider.Should().BeOfType<Fake>().Which.Discoveries.Should().Be(0));
        await using var empty = ProviderRouting.Build([], root.Path);
        empty.Providers().Should().BeEmpty();
    }

    [Fact]
    public async Task FullRegistryRetainsDirectSearchButExcludesProviderFromContext()
    {
        using var root = new Root();
        await using var registry = ProviderRouting.Build(
            [new("heavy", "harness", () => new Fake("heavy", [Capabilities.TextSearch]), Context: false)],
            root.Path
        );
        registry.InContext("heavy").Should().BeFalse();
        (await registry.CallAsync(new(Capabilities.TextSearch), cancellationToken: Token)).Should().ContainSingle();
        (await registry.CallAsync(new(Capabilities.TextSearch), name => !registry.InContext(name), Token))
            .Should()
            .BeEmpty();
    }

    [Fact]
    public async Task LanguageMismatchNeverDiscoversStartsOrProbesAdapter()
    {
        await using var fake = new Fake("a", [Capabilities.SymbolSearch]);
        await using var routed = ProviderRouting.Route(fake, ["csharp"], [], ["typescript"]);
        routed.Capabilities().Should().BeEmpty();
        var health = await routed.HealthAsync(true, Token);
        health.State.Should().Be("idle");
        health.Detail.Should().Be("not used here: serves csharp; project: typescript");
        await FluentActions
            .Awaiting(() => routed.ExecuteAsync(new(Capabilities.SymbolSearch), Token))
            .Should()
            .ThrowExactlyAsync<UnsupportedCapabilityException>();
        fake.Discoveries.Should().Be(0);
        fake.Probes.Should().Be(0);
        fake.Calls.Should().Be(0);
        ProviderRouting
            .Inactive(["csharp"], [])
            .Should()
            .Be("not used here: serves csharp; project: no recognized language");
        ProviderRouting.Route(fake, ["csharp"], [], ["csharp"]).Should().BeSameAs(fake);
    }

    [Fact]
    public async Task OnlyHidesCapabilitiesAndRejectsDirectExecutionWhilePreservingVersion()
    {
        await using var fake = new Fake("a", [Capabilities.SymbolSearch, Capabilities.Callers]);
        await using var routed = ProviderRouting.Route(fake, [], [Capabilities.Callers], []);
        routed.Capabilities().Should().ContainSingle().Which.Capability.Should().Be(Capabilities.Callers);
        await FluentActions
            .Awaiting(() => routed.ExecuteAsync(new(Capabilities.SymbolSearch), Token))
            .Should()
            .ThrowExactlyAsync<UnsupportedCapabilityException>();
        await routed.ExecuteAsync(new(Capabilities.Callers), Token);
        fake.Calls.Should().Be(1);
        routed.Should().BeAssignableTo<IVersionedProvider>().Which.ServerVersion.Should().Be("fixture-1");
        (routed is IProviderRefresher).Should().BeFalse();
    }

    [Fact]
    public async Task RefreshIsPreservedAndOnlyLanguageMismatchSkipsIt()
    {
        await using var fake = new RefreshingFake();
        await using var inactive = ProviderRouting.Route(fake, ["csharp"], [], ["typescript"]);
        (await inactive.Should().BeAssignableTo<IProviderRefresher>().Which.RefreshAsync(new(true, ["a.cs"]), Token))
            .Skipped.Should()
            .BeTrue();
        fake.Refreshes.Should().Be(0);
        await using var active = ProviderRouting.Route(fake, [], [Capabilities.TextSearch], []);
        (await active.Should().BeAssignableTo<IProviderRefresher>().Which.RefreshAsync(new(true, ["a.cs"]), Token))
            .Skipped.Should()
            .BeFalse();
        fake.Refreshes.Should().Be(1);
    }

    [Fact]
    public async Task UnavailableProviderHasItsOwnFailureResult()
    {
        using var root = new Root();
        await using var registry = ProviderRouting.Build(
            [
                new(
                    "missing",
                    "mcp",
                    () =>
                        new Fake(
                            "missing",
                            [Capabilities.TextSearch],
                            (_, _) => throw new ProviderException("missing", "cannot start", hint: "install server"),
                            health: "unavailable"
                        )
                ),
                new("ready", "mcp", () => new Fake("ready", [Capabilities.TextSearch])),
            ],
            root.Path
        );
        var results = await registry.CallAsync(new(Capabilities.TextSearch), cancellationToken: Token);
        (await registry.Providers()[0].HealthAsync(false, Token)).State.Should().Be("unavailable");
        results[0].Error!.Message.Should().Contain("install server");
        results[1].Error.Should().BeNull();
    }

    [Fact]
    public void LanguageDetectionStopsAtOneDirectoryLevelAndUsesAllPinnedMarkers()
    {
        using var root = new Root();
        root.Write("App.slnx", "");
        root.Write("src/package.json", "");
        root.Write("src/pyproject.toml", "");
        root.Write("src/build.gradle.kts", "");
        ProviderRouting.DetectLanguages(root.Path).Should().Equal("csharp", "java", "python", "typescript");
        using var deep = new Root();
        deep.Write("src/Api/Api.csproj", "");
        deep.Write("Wrong.CSPROJ", "");
        ProviderRouting.DetectLanguages(deep.Path).Should().BeEmpty();
    }

    [Fact]
    public void NestedWorktreeFilterKeepsSubmodulesAndOutsidePathsAndRefreshesCache()
    {
        using var root = new Root();
        root.Write(".claude/worktrees/x/.git", "gitdir: C:/checkout/.git/worktrees/x\n");
        root.Write("vendor/sub/.git", "gitdir: ../../.git/modules/sub\n");
        Directory.CreateDirectory(Path.Combine(root.Path, "nested", ".git"));
        var clock = new Clock();
        var filter = new NestedWorktreeFilter(root.Path, clock);
        filter.Skip(".claude/worktrees/x/a.cs").Should().BeTrue();
        filter.Skip(Path.Combine(root.Path, ".claude", "worktrees", "x", "a.cs")).Should().BeTrue();
        filter.Skip("vendor/sub/a.cs").Should().BeFalse();
        filter.Skip("nested/a.cs").Should().BeFalse();
        filter.Skip("src/a.cs").Should().BeFalse();
        filter.Skip(Path.Combine(root.Path, "..", "outside", "a.cs")).Should().BeFalse();
        filter.Skip("../outside/a.cs").Should().BeFalse();
        root.Write("src/.git", "gitdir: C:/checkout/.git/worktrees/new\n");
        filter.Skip("src/a.cs").Should().BeFalse();
        clock.Now = clock.Now.AddSeconds(30);
        filter.Skip("src/a.cs").Should().BeTrue();
    }

    private sealed class RefreshingFake()
        : Fake("refresh", [Core.Providers.Capabilities.TextSearch]),
            IProviderRefresher
    {
        public int Refreshes { get; private set; }

        public Task<RefreshResult> RefreshAsync(RefreshRequest request, CancellationToken cancellationToken = default)
        {
            request.Full.Should().BeTrue();
            request.Changed.Should().Equal("a.cs");
            Refreshes++;
            return Task.FromResult(new RefreshResult());
        }
    }

    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = DateTimeOffset.UtcNow;

        public override DateTimeOffset GetUtcNow() => Now;
    }

    internal sealed class Root : IDisposable
    {
        public string Path { get; } =
            System.IO.Path.Combine(System.IO.Path.GetTempPath(), "harness-providers-" + Guid.NewGuid().ToString("N"));

        public Root() => Directory.CreateDirectory(Path);

        public void Write(string relative, string text)
        {
            var file = System.IO.Path.Combine(Path, relative.Replace('/', System.IO.Path.DirectorySeparatorChar));
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(file)!);
            File.WriteAllText(file, text);
        }

        public void Dispose() => Directory.Delete(Path, true);
    }
}
