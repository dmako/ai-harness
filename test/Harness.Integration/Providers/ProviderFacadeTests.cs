using System.Text.Json;
using System.Diagnostics.CodeAnalysis;
using AwesomeAssertions;
using Harness.Core.Projects;
using Harness.Core.Providers;
using Harness.Infrastructure.Configuration;
using Harness.Infrastructure.Processes;
using Harness.Infrastructure.Projects;
using Harness.Infrastructure.Providers;
using Harness.Infrastructure.Storage;
using Harness.Mcp.Providers;
using Harness.Providers.Packs;
using Harness.Providers.Registry;
using Xunit;
using Fake = Harness.Integration.Providers.ProviderRegistryTests.Fake;

namespace Harness.Integration.Providers;

[SuppressMessage("Reliability", "CA2000:Dispose objects before losing scope", Justification = "Registered fake providers are owned and disposed by each awaited registry.")]
public sealed class ProviderFacadeTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;
    private static async Task<JsonElement> Call(ProviderTools tools, string tool, string json = """{}""") => JsonSerializer.SerializeToElement(await tools.CallAsync(tool, JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json), Token));

    [Fact]
    public async Task SearchMergesPartialErrorsKeepsProvenanceAndOnlyEmitsDetailsInDebug()
    {
        await using var registry = new ProviderRegistry();
        registry.Register(new Fake("graph", [Capabilities.SymbolSearch], execute: (request, _) =>
        {
            request.Query.Should().Be("Alpha");
            request.Limit.Should().Be(2);
            return Task.FromResult(new ProviderResult { Bytes = 123, Truncated = true, Items = [new() { Kind = "symbol", Name = "Alpha", QualifiedName = "A.Alpha", Path = "src/A.cs", StartLine = 7, Provider = "graph", Tool = "find", Score = 0.8 }] });
        }));
        registry.Register(new Fake("grep", [Capabilities.TextSearch], execute: (_, _) => Task.FromResult(new ProviderResult { Error = new ProviderException("grep", "not installed\nextra") })));
        var tools = new ProviderTools(registry);
        var result = await Call(tools, "search_project", """{"query":" Alpha ","limit":2}""");
        result.GetProperty("results").GetArrayLength().Should().Be(1);
        result.GetProperty("results")[0].TryGetProperty("tool", out _).Should().BeFalse();
        result.GetProperty("errors")[0].GetString().Should().Be("grep: not installed");
        result.GetProperty("truncated").GetBoolean().Should().BeTrue();
        result.TryGetProperty("calls", out _).Should().BeFalse();
        var debug = await Call(tools, "search_project", """{"query":"Alpha","limit":2,"debug":true}""");
        debug.GetProperty("calls").GetArrayLength().Should().Be(2);
        debug.GetProperty("calls")[0].GetProperty("bytes").GetInt32().Should().Be(123);
        debug.GetProperty("results")[0].GetProperty("qualified_name").GetString().Should().Be("A.Alpha");
    }

    [Fact]
    public async Task NotIndexedIsDegradedAndCrossBranchResultsAndStructureAreWithheld()
    {
        await using var registry = new ProviderRegistry();
        registry.Register(new Fake("index", [Capabilities.SymbolSearch, Capabilities.Overview], freshness: "indexed", execute: (_, _) => Task.FromResult(new ProviderResult { Items = [new() { Kind = "symbol", Name = "OtherBranch", Provider = "index" }], Summary = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>("""{"nodes":2}""") })));
        registry.Register(new Fake("live", [Capabilities.SymbolSearch], execute: (_, _) => Task.FromResult(new ProviderResult { Items = [new() { Kind = "symbol", Name = "Current", Provider = "live" }] })));
        var tools = new ProviderTools(registry, new Snapshot(new("root", Stale: "branch changed", CrossBranch: true)));
        var search = await Call(tools, "search_project", """{"query":"x","kind":"symbol"}""");
        search.GetProperty("results").GetArrayLength().Should().Be(1);
        search.GetProperty("results")[0].GetProperty("name").GetString().Should().Be("Current");
        search.GetProperty("stale").GetString().Should().Contain("index were withheld");
        var overview = await Call(tools, "project_overview");
        overview.TryGetProperty("structure", out _).Should().BeFalse();
        registry.Register(new Fake("missing", [Capabilities.Callers], execute: (_, _) => Task.FromResult(new ProviderResult { Error = new ProviderNotIndexedException("not indexed yet") })));
        var empty = await Call(tools, "search_project", """{"query":"x","kind":"callers"}""");
        empty.TryGetProperty("errors", out _).Should().BeFalse();
        empty.GetProperty("note").GetString().Should().Contain("not indexed yet").And.Contain("try kind text");
    }

    [Fact]
    public async Task PackagesRouteOnlyToPackageCapabilitiesAndUnsupportedActionsStayExplicit()
    {
        await using var registry = new ProviderRegistry();
        registry.Register(new Fake("nuget", [Capabilities.PackageTypeDefinition, Capabilities.PackageInfo], execute: (request, _) =>
        {
            if (request.Capability == Capabilities.PackageInfo)
                return Task.FromResult(new ProviderResult { Error = new IOException("private feed unavailable") });
            request.Package.Should().Be("Foo");
            request.Version.Should().Be("1.2");
            request.Type.Should().Be("Result");
            request.Limit.Should().Be(50);
            return Task.FromResult(new ProviderResult { Provider = "nuget", Text = "public class Result" });
        }));
        var tools = new ProviderTools(registry);
        var result = await Call(tools, "package_api", """{"action":"definition","package":" Foo ","version":" 1.2 ","type":"Result","limit":500}""");
        result.GetProperty("answers")[0].GetProperty("text").GetString().Should().Be("public class Result");
        var failed = await Call(tools, "package_api", """{"action":"info","package":"Foo"}""");
        failed.GetProperty("note").GetString().Should().Contain("no package provider could answer");
        var unsupported = await Call(tools, "package_api", """{"action":"types","package":"Foo"}""");
        unsupported.GetProperty("note").GetString().Should().Contain("no provider offers package.types");
    }

    [Theory]
    [InlineData("search_project", """{"query":" "}""", "query must not be empty")]
    [InlineData("search_project", """{"query":"x","kind":"bad"}""", "kind must be")]
    [InlineData("search_project", """{"query":"x","path":"../outside"}""", "path must be relative")]
    [InlineData("search_project", """{"query":"x","scope":"bad"}""", "scope must be")]
    [InlineData("package_api", """{"action":"search"}""", "search needs query")]
    [InlineData("package_api", """{"action":"info"}""", "info needs package")]
    [InlineData("package_api", """{"action":"definition","package":"Foo"}""", "definition needs type")]
    public async Task InvalidFacadeArgumentsAreRejected(string tool, string args, string error) =>
        await FluentActions.Awaiting(() => Call(new(), tool, args)).Should().ThrowAsync<ArgumentException>().WithMessage("*" + error + "*");

    [Fact]
    public async Task EmbeddedPacksMatchFrozenSourceAndOverridesRouteCodeAndPackagesLazily()
    {
        using var fixture = new ProviderFixture();
        using var baseline = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(ProviderFixture.RepositoryRoot, "doc", "port", "inventory", "providers.json"), Token));
        foreach (var pack in ConfigurationLoader.Packs)
        {
            var current = Path.Combine(ProviderFixture.RepositoryRoot, "src", "Harness.Infrastructure", "Configuration", "Packs", pack + ".toml");
            (await File.ReadAllTextAsync(current, Token)).ReplaceLineEndings("\n").Should().Be(baseline.RootElement.GetProperty("packs").EnumerateArray().Single(entry => entry.GetProperty("path").GetString()!.EndsWith("/" + pack + ".toml", StringComparison.Ordinal)).GetProperty("toml").GetString()!.ReplaceLineEndings("\n"));
        }
        ConfigurationLoader.Packs.Should().Equal("codebase-memory", "graphify-csharp", "nuget", "rg-fd", "ripwire");
        var file = Path.Combine(fixture.Root, "config.toml");
        fixture.Write("App.csproj", "<Project />");
        fixture.Write("config.toml", """
            [providers.codebase-memory]
            command = "overridden"
            only = ["code.symbol_search"]
            [providers.nuget]
            command = "overridden"
            [providers.disabled]
            pack = "ripwire"
            enabled = false
            """);
        var configuration = ConfigurationLoader.LoadFiles("", file).Value;
        var factory = new CountingFactory();
        await using var registry = ConfiguredProviders.Build(configuration.Providers, fixture.Root, factory);
        registry.Providers().Select(provider => provider.Name).Should().Equal("codebase-memory", "nuget");
        registry.For(Capabilities.SymbolSearch).Should().ContainSingle().Which.Name.Should().Be("codebase-memory");
        registry.For(Capabilities.PackageInfo).Should().ContainSingle().Which.Name.Should().Be("nuget");
        registry.For(Capabilities.TextSearch).Should().BeEmpty();
        factory.Ids.Should().BeEmpty();
    }

    [Fact]
    public async Task FacadeMetadataAndRecorderNeverInitializeStorageButReadExistingStatistics()
    {
        using var fixture = new ProviderFixture();
        var lifecycle = new ProjectLifecycle(Path.Combine(fixture.Root, "global", "config.toml"));
        var location = await lifecycle.LocateAsync(fixture.Root, allowNonGit: true, Token);
        await using var session = new HarnessStateSession(location, lifecycle.Registry, new("test", "", "test", "test"));
        var tools = new ProviderTools(project: session);
        (await Call(tools, "project_overview")).GetProperty("project_root").GetString().Should().Be(fixture.Root);
        await Call(tools, "provider_status");
        await Call(tools, "search_project", """{"query":"x"}""");
        await using (var recorder = new ProviderCallRecorder(location.DatabasePath))
            recorder.Observe(new() { Provider = "p" }, new(Capabilities.SymbolSearch));
        Directory.Exists(Path.Combine(fixture.Root, ".harness")).Should().BeFalse();
        using (var database = HarnessDatabase.Open(location.DatabasePath, cancellationToken: Token))
            database.SchemaVersion().Should().Be(19);
        await using (var recorder = new ProviderCallRecorder(location.DatabasePath))
        {
            recorder.Observe(new() { Provider = "p", Capability = Capabilities.SymbolSearch, Bytes = 12, Latency = TimeSpan.FromMilliseconds(10) }, new(Capabilities.SymbolSearch, "query"));
            recorder.Observe(new() { Provider = "p", Capability = Capabilities.SymbolSearch, Error = new IOException("failed"), Bytes = 4 }, new(Capabilities.SymbolSearch));
        }
        var stats = await session.ReadProviderStatisticsAsync("p", Token);
        stats["recent_24h"].GetProperty("calls").GetInt32().Should().Be(2);
        stats["recent_24h"].GetProperty("errors").GetInt32().Should().Be(1);
        stats["recent_24h"].GetProperty("avg_bytes").GetInt32().Should().Be(8);
    }

    private sealed class Snapshot(ProviderProjectSnapshot snapshot) : IProviderProjectSession
    {
        public Task<ProviderProjectSnapshot> ReadProviderProjectAsync(CancellationToken cancellationToken) => Task.FromResult(snapshot);
        public Task<IReadOnlyDictionary<string, JsonElement>> ReadProviderStatisticsAsync(string provider, CancellationToken cancellationToken) => Task.FromResult<IReadOnlyDictionary<string, JsonElement>>(new Dictionary<string, JsonElement>());
    }
}
