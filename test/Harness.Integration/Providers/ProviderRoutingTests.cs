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
        ProviderRegistration[] entries = [new("disabled", "mcp", Forbidden, Enabled: false), new("unknown", "other", Forbidden),
            new("heavy", "harness", Forbidden, Context: false), new("z", "mcp", () => new Fake("z", [Capabilities.TextSearch])),
            new("a", "mcp", () => new Fake("a", [Capabilities.SymbolSearch]))];
        await using var registry = ProviderRouting.Build(entries, root.Path, light: true);
        Assert.Equal(["a", "z"], registry.Providers().Select(provider => provider.Name));
        Assert.All(registry.Providers(), provider => Assert.Equal(0, Assert.IsType<Fake>(provider).Discoveries));
        await using var empty = ProviderRouting.Build([], root.Path);
        Assert.Empty(empty.Providers());
    }

    [Fact]
    public async Task FullRegistryRetainsDirectSearchButExcludesProviderFromContext()
    {
        using var root = new Root();
        await using var registry = ProviderRouting.Build([new("heavy", "harness", () => new Fake("heavy", [Capabilities.TextSearch]), Context: false)], root.Path);
        Assert.False(registry.InContext("heavy"));
        Assert.Single(await registry.CallAsync(new(Capabilities.TextSearch), cancellationToken: Token));
        Assert.Empty(await registry.CallAsync(new(Capabilities.TextSearch), name => !registry.InContext(name), Token));
    }

    [Fact]
    public async Task LanguageMismatchNeverDiscoversStartsOrProbesAdapter()
    {
        await using var fake = new Fake("a", [Capabilities.SymbolSearch]);
        await using var routed = ProviderRouting.Route(fake, ["csharp"], [], ["typescript"]);
        Assert.Empty(routed.Capabilities());
        var health = await routed.HealthAsync(true, Token);
        Assert.Equal("idle", health.State);
        Assert.Equal("not used here: serves csharp; project: typescript", health.Detail);
        await Assert.ThrowsAsync<UnsupportedCapabilityException>(() => routed.ExecuteAsync(new(Capabilities.SymbolSearch), Token));
        Assert.Equal(0, fake.Discoveries);
        Assert.Equal(0, fake.Probes);
        Assert.Equal(0, fake.Calls);
        Assert.Equal("not used here: serves csharp; project: no recognized language", ProviderRouting.Inactive(["csharp"], []));
        Assert.Same(fake, ProviderRouting.Route(fake, ["csharp"], [], ["csharp"]));
    }

    [Fact]
    public async Task OnlyHidesCapabilitiesAndRejectsDirectExecutionWhilePreservingVersion()
    {
        await using var fake = new Fake("a", [Capabilities.SymbolSearch, Capabilities.Callers]);
        await using var routed = ProviderRouting.Route(fake, [], [Capabilities.Callers], []);
        Assert.Equal(Capabilities.Callers, Assert.Single(routed.Capabilities()).Capability);
        await Assert.ThrowsAsync<UnsupportedCapabilityException>(() => routed.ExecuteAsync(new(Capabilities.SymbolSearch), Token));
        await routed.ExecuteAsync(new(Capabilities.Callers), Token);
        Assert.Equal(1, fake.Calls);
        Assert.Equal("fixture-1", Assert.IsAssignableFrom<IVersionedProvider>(routed).ServerVersion);
        Assert.False(routed is IProviderRefresher);
    }

    [Fact]
    public async Task RefreshIsPreservedAndOnlyLanguageMismatchSkipsIt()
    {
        await using var fake = new RefreshingFake();
        await using var inactive = ProviderRouting.Route(fake, ["csharp"], [], ["typescript"]);
        Assert.True((await Assert.IsAssignableFrom<IProviderRefresher>(inactive).RefreshAsync(new(true, ["a.cs"]), Token)).Skipped);
        Assert.Equal(0, fake.Refreshes);
        await using var active = ProviderRouting.Route(fake, [], [Capabilities.TextSearch], []);
        Assert.False((await Assert.IsAssignableFrom<IProviderRefresher>(active).RefreshAsync(new(true, ["a.cs"]), Token)).Skipped);
        Assert.Equal(1, fake.Refreshes);
    }

    [Fact]
    public async Task UnavailableProviderHasItsOwnFailureResult()
    {
        using var root = new Root();
        await using var registry = ProviderRouting.Build([new("missing", "mcp", () => new Fake("missing", [Capabilities.TextSearch], (_, _) => throw new ProviderException("missing", "cannot start", hint: "install server"), health: "unavailable")),
            new("ready", "mcp", () => new Fake("ready", [Capabilities.TextSearch]))], root.Path);
        var results = await registry.CallAsync(new(Capabilities.TextSearch), cancellationToken: Token);
        Assert.Equal("unavailable", (await registry.Providers()[0].HealthAsync(false, Token)).State);
        Assert.Contains("install server", results[0].Error!.Message, StringComparison.Ordinal);
        Assert.Null(results[1].Error);
    }

    [Fact]
    public void LanguageDetectionStopsAtOneDirectoryLevelAndUsesAllPinnedMarkers()
    {
        using var root = new Root();
        root.Write("App.slnx", "");
        root.Write("src/package.json", "");
        root.Write("src/go.mod", "");
        root.Write("src/pyproject.toml", "");
        root.Write("src/build.gradle.kts", "");
        Assert.Equal(["csharp", "go", "java", "python", "typescript"], ProviderRouting.DetectLanguages(root.Path));
        using var deep = new Root();
        deep.Write("src/Api/Api.csproj", "");
        deep.Write("Wrong.CSPROJ", "");
        Assert.Empty(ProviderRouting.DetectLanguages(deep.Path));
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
        Assert.True(filter.Skip(".claude/worktrees/x/a.cs"));
        Assert.True(filter.Skip(Path.Combine(root.Path, ".claude", "worktrees", "x", "a.cs")));
        Assert.False(filter.Skip("vendor/sub/a.cs"));
        Assert.False(filter.Skip("nested/a.cs"));
        Assert.False(filter.Skip("src/a.cs"));
        Assert.False(filter.Skip(Path.Combine(root.Path, "..", "outside", "a.cs")));
        Assert.False(filter.Skip("../outside/a.cs"));
        root.Write("src/.git", "gitdir: C:/checkout/.git/worktrees/new\n");
        Assert.False(filter.Skip("src/a.cs"));
        clock.Now = clock.Now.AddSeconds(30);
        Assert.True(filter.Skip("src/a.cs"));
    }

    private sealed class RefreshingFake() : Fake("refresh", [Core.Providers.Capabilities.TextSearch]), IProviderRefresher
    {
        public int Refreshes { get; private set; }
        public Task<RefreshResult> RefreshAsync(RefreshRequest request, CancellationToken cancellationToken = default)
        {
            Assert.True(request.Full);
            Assert.Equal(["a.cs"], request.Changed);
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
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "harness-providers-" + Guid.NewGuid().ToString("N"));
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
