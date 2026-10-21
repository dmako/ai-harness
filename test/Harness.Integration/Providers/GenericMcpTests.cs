using System.Text.Json;
using AwesomeAssertions;
using Harness.Core.Providers;
using Harness.Infrastructure.Configuration;
using Harness.Infrastructure.Processes;
using Harness.Providers.Mcp;
using Xunit;

namespace Harness.Integration.Providers;

[Collection("Provider processes")]
public sealed class GenericMcpTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;
    private static Dictionary<string, JsonElement> Arguments(string json) => JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json)!;
    private static ProviderConfiguration Configuration(params (string Capability, CapabilityMapping Mapping)[] mappings) => new()
    {
        Type = "mcp",
        Command = "dotnet",
        Args = [],
        TimeoutMs = 10000,
        Capabilities = mappings.ToDictionary(pair => pair.Capability, pair => pair.Mapping, StringComparer.Ordinal),
    };
    private static IProvider Provider(ProviderFixture fixture, ProviderConfiguration configuration, bool linked = false) => GenericMcpProvider.Build("fakeidx", configuration with { Args = [ProviderFixture.Worker, "normal", fixture.Root] }, fixture.Root, new ProviderProcessFactory(), linkedWorktree: linked);

    [Fact]
    public async Task ArgumentsFieldsFilteringNumericConversionsAndProvenanceMatchGenericContract()
    {
        using var fixture = new ProviderFixture();
        fixture.Write("symbols.json", JsonSerializer.Serialize(new
        {
            data = new
            {
                hits = new object[]
        {
            new { sym = new { name = "First", fqn = "Service.First" }, loc = new { file = Path.Combine(fixture.Root, "src", "A.cs"), line = 603 }, rank = 0.9 },
            new { sym = new { name = "Second" }, loc = new { file = "src/B.cs", line = "720" }, rank = "0.5" },
            new { sym = new { name = "Outside" }, loc = new { file = "web/C.cs", line = 1 }, rank = 0.3 },
        }
            }
        }));
        var mapping = new CapabilityMapping
        {
            Tool = "symbols",
            Items = "data.hits",
            Args = Arguments("""{"name":"{query}","short":"{query_name}","regex":"{query_regex}","limit":"{limit}","more":"{limit_more}","scope":"{path}","root":"{root}","target":"{target_name}","budget":"{budget}","unknown":"{unknown}","exact":false,"note":"q={query}"}"""),
            Fields = new(StringComparer.Ordinal) { ["name"] = "sym.name", ["qualified_name"] = "sym.fqn", ["path"] = "loc.file", ["start_line"] = "loc.line", ["score"] = "rank" },
            Evidence = "exact",
        };
        await using var provider = Provider(fixture, Configuration((Capabilities.SymbolSearch, mapping)) with { Dir = "project", Freshness = "indexed" });
        (await provider.HealthAsync(false, Token)).State.Should().Be("idle");
        provider.Capabilities().Should().ContainSingle().Which.Should().Be(new CapabilityInfo(Capabilities.SymbolSearch, "fakeidx", "medium", "medium", "indexed", "project", "exact"));
        var result = await provider.ExecuteAsync(new(Capabilities.SymbolSearch, "Service.First", Limit: 2, Target: "Target.Last", Budget: 1234), Token);
        result.Error.Should().BeNull();
        result.Truncated.Should().BeTrue();
        result.Items.Select(item => item.Name).Should().Equal("First", "Second");
        result.Items[0].Should().Be(new ProviderItem { Kind = "symbol", Name = "First", QualifiedName = "Service.First", Path = "src/A.cs", StartLine = 603, Score = 0.9, Provider = "fakeidx", Tool = "symbols" });
        result.Items[1].StartLine.Should().Be(720);
        result.Items[1].Score.Should().Be(0.5);
        using var args = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(fixture.Root, "symbols.args"), Token));
        args.RootElement.GetProperty("limit").GetInt32().Should().Be(2);
        args.RootElement.GetProperty("more").GetInt32().Should().Be(3);
        args.RootElement.GetProperty("budget").GetInt32().Should().Be(1234);
        args.RootElement.GetProperty("short").GetString().Should().Be("First");
        args.RootElement.GetProperty("target").GetString().Should().Be("Last");
        args.RootElement.GetProperty("regex").GetString().Should().Be(@"Service\.First");
        args.RootElement.TryGetProperty("scope", out _).Should().BeFalse();
        args.RootElement.TryGetProperty("unknown", out _).Should().BeFalse();
        var filtered = await provider.ExecuteAsync(new(Capabilities.SymbolSearch, "x", Path: "web"), Token);
        filtered.Items.Should().ContainSingle().Which.Name.Should().Be("Outside");
        await FluentActions.Awaiting(() => provider.ExecuteAsync(new(Capabilities.SymbolSearch, Path: "../outside"), Token)).Should().ThrowAsync<ArgumentException>();
    }

    [Fact]
    public async Task WildcardKeysWhereFiltersAndPerProviderLimitAreDeterministic()
    {
        using var fixture = new ProviderFixture();
        fixture.Write("search.json", """{"truncated":false,"matches":{"src/B.cs":{"lines":[{"line":26,"text":" first ","is_match":true,"count":"1"},{"line":27,"text":"context","is_match":false,"count":1}]},"src/A.cs":{"lines":[{"line":3,"text":"earlier","is_match":true,"count":1}]}}}""");
        var mapping = new CapabilityMapping { Tool = "search", Items = "matches.*.lines", Where = Arguments("""{"is_match":true,"count":1}"""), Fields = new(StringComparer.Ordinal) { ["path"] = "$key", ["start_line"] = "line", ["snippet"] = "text" }, Truncated = "truncated" };
        await using var provider = Provider(fixture, Configuration((Capabilities.TextSearch, mapping)));
        var result = await provider.ExecuteAsync(new(Capabilities.TextSearch), Token);
        result.Items.Select(item => item.Path).Should().Equal("src/A.cs", "src/B.cs");
        result.Items[1].Snippet.Should().Be("first");
        (await provider.ExecuteAsync(new(Capabilities.TextSearch, Limit: 1), Token)).Truncated.Should().BeTrue();
        fixture.Write("search.json", """{"matches":{}}""");
        var empty = await provider.ExecuteAsync(new(Capabilities.TextSearch), Token);
        empty.Error.Should().BeNull();
        empty.Items.Should().BeEmpty();
    }

    [Fact]
    public async Task StructuredContentWinsAndOverviewArraysRemainStructured()
    {
        using var fixture = new ProviderFixture();
        fixture.Write("map.result.json", """{"content":[{"type":"text","text":"not JSON"}],"structuredContent":{"modules":12,"entry":"Program.cs"}}""");
        await using var provider = Provider(fixture, Configuration((Capabilities.Overview, new() { Tool = "map" })));
        var result = await provider.ExecuteAsync(new(Capabilities.Overview), Token);
        result.Error.Should().BeNull();
        result.Summary!["modules"].GetInt32().Should().Be(12);
        result.Bytes.Should().Be(8);
        fixture.Write("map.result.json", """{"content":[{"type":"text","text":"[1,2]"}]}""");
        (await provider.ExecuteAsync(new(Capabilities.Overview), Token)).Summary!["result"].GetArrayLength().Should().Be(2);
    }

    [Fact]
    public async Task TextPatternsDecodeEntitiesAndReportTruncation()
    {
        using var fixture = new ProviderFixture();
        fixture.Write("find.json", "src/A.cs:3:Alpha&lt;T&gt;\nsrc/B.cs:8:Beta\nmore results available");
        var mapping = new CapabilityMapping { Tool = "find", Pattern = @"(?m)^(?P<path>[^:]+):(?P<start_line>\d+):(?P<name>[^\n]+)$", Truncated = "more results" };
        await using var provider = Provider(fixture, Configuration((Capabilities.SymbolSearch, mapping)));
        var result = await provider.ExecuteAsync(new(Capabilities.SymbolSearch), Token);
        result.Error.Should().BeNull();
        result.Truncated.Should().BeTrue();
        result.Items[0].Name.Should().Be("Alpha<T>");
        result.Items[0].StartLine.Should().Be(3);
        result.Items.Select(item => item.Path).Should().Equal("src/A.cs", "src/B.cs");
    }

    [Fact]
    public async Task DocumentFallbackAndAllChainsKeepOrderAndByteCounts()
    {
        using var fixture = new ProviderFixture();
        fixture.Write("class.json", "Class not found in package X.");
        fixture.Write("interface.json", "public interface IExample { } 🧪");
        fixture.Write("unused.json", "must not run");
        fixture.Write("classes.json", """{"types":[{"name":"Example"}]}""");
        fixture.Write("interfaces.json", """{"types":[{"name":"IExample"}]}""");
        var document = new CapabilityMapping { Tool = "class", Miss = "not found", Chain = [new() { Tool = "interface" }, new() { Tool = "unused" }] };
        var types = new CapabilityMapping { Tool = "classes", Items = "types", Fields = new(StringComparer.Ordinal) { ["name"] = "name" }, Combine = "all", Chain = [new() { Tool = "interfaces", Items = "types", Fields = new(StringComparer.Ordinal) { ["name"] = "name" } }] };
        await using var provider = Provider(fixture, Configuration((Capabilities.PackageTypeDefinition, document), (Capabilities.PackageTypes, types)));
        var result = await provider.ExecuteAsync(new(Capabilities.PackageTypeDefinition, Package: "X", Type: "IExample"), Token);
        result.Error.Should().BeNull();
        result.Text.Should().Be("public interface IExample { } 🧪");
        result.Bytes.Should().Be(System.Text.Encoding.UTF8.GetByteCount("Class not found in package X.public interface IExample { } 🧪"));
        File.Exists(Path.Combine(fixture.Root, "unused.args")).Should().BeFalse();
        var all = await provider.ExecuteAsync(new(Capabilities.PackageTypes), Token);
        all.Items.Select(item => item.Name).Should().Equal("Example", "IExample");
        provider.Capabilities().Should().AllSatisfy(info => info.Scope.Should().Be("external"));
    }

    [Fact]
    public async Task MatchedCodeMissesAreEmptyButToolAndMappingErrorsStayDistinct()
    {
        using var fixture = new ProviderFixture();
        fixture.Write("missing.json", "symbol not found");
        fixture.Write("missing.protocol-error", "symbol not found");
        fixture.Write("error.json", """{"error":"index missing","hint":"index first"}""");
        fixture.Write("error.error", "");
        fixture.Write("plain.json", "not JSON");
        fixture.Write("wrong.json", """{"other":[]}""");
        fixture.Write("mapped.json", """{"message":"mapping error"}""");
        await using var provider = Provider(fixture, Configuration(
            (Capabilities.Callers, new() { Tool = "missing", Miss = "not found" }),
            (Capabilities.PackageInfo, new() { Tool = "error" }),
            (Capabilities.TextSearch, new() { Tool = "plain", Items = "hits" }),
            (Capabilities.SymbolSearch, new() { Tool = "wrong", Items = "hits" }),
            (Capabilities.FileSearch, new() { Tool = "mapped", Error = "message" })));
        var empty = await provider.ExecuteAsync(new(Capabilities.Callers), Token);
        empty.Error.Should().BeNull();
        empty.Items.Should().BeEmpty();
        var error = (await provider.ExecuteAsync(new(Capabilities.PackageInfo), Token)).Error.Should().BeOfType<ProviderException>().Which;
        error.Hint.Should().Be("index first");
        error.Tool.Should().Be("error");
        (await provider.ExecuteAsync(new(Capabilities.TextSearch), Token)).Error!.Message.Should().Contain("not JSON");
        (await provider.ExecuteAsync(new(Capabilities.SymbolSearch), Token)).Error!.Message.Should().Contain("no list at \"hits\"");
        (await provider.ExecuteAsync(new(Capabilities.FileSearch), Token)).Error!.Message.Should().Contain("mapping error");
        await FluentActions.Awaiting(() => provider.ExecuteAsync(new(Capabilities.Impact), Token)).Should().ThrowAsync<UnsupportedCapabilityException>();
    }

    [Fact]
    public async Task ProbeVerifiesChainedToolsWithoutChangingDeclaredCapabilities()
    {
        using var fixture = new ProviderFixture();
        fixture.Write("present.json", "[]");
        await using var provider = Provider(fixture, Configuration((Capabilities.SymbolSearch, new() { Tool = "present", Chain = [new() { Tool = "absent" }] })));
        (await provider.HealthAsync(false, Token)).State.Should().Be("idle");
        var health = await provider.HealthAsync(true, Token);
        health.State.Should().Be("degraded");
        health.Detail.Should().Contain("code.symbol_search -> absent");
        provider.Capabilities().Should().ContainSingle();
        provider.Should().NotBeAssignableTo<IProviderRefresher>();
    }

    [Fact]
    public async Task ResolutionCachesOnlyHitsAndRefreshHonorsWorktreeOptIn()
    {
        using var fixture = new ProviderFixture();
        fixture.Write("projects.json", """{"projects":[]}""");
        fixture.Write("symbols.json", "[]");
        fixture.Write("refresh.json", "indexed");
        var configuration = Configuration((Capabilities.SymbolSearch, new() { Tool = "symbols", Args = Arguments("""{"project":"{project}"}""") })) with
        {
            Resolve = new() { Tool = "projects", Items = "projects", Match = "root", Value = "name", Fix = "index {root}" },
            Refresh = new() { Tool = "refresh", Args = Arguments("""{"root":"{root}","full":"{full}","changed":"{changed}"}"""), Skip = "not indexed" },
        };
        await using var provider = Provider(fixture, configuration, linked: true);
        await FluentActions.Awaiting(() => provider.ExecuteAsync(new(Capabilities.SymbolSearch), Token)).Should().ThrowAsync<ProviderNotIndexedException>();
        var refresher = provider.Should().BeAssignableTo<IProviderRefresher>().Which;
        (await refresher.RefreshAsync(new(), Token)).Skipped.Should().BeTrue();
        File.Exists(Path.Combine(fixture.Root, "refresh.args")).Should().BeFalse();
        fixture.Write("projects.json", JsonSerializer.Serialize(new { projects = new[] { new { root = fixture.Root, name = "known" } } }));
        (await provider.ExecuteAsync(new(Capabilities.SymbolSearch), Token)).Error.Should().BeNull();
        fixture.Write("projects.json", """{"projects":[]}""");
        (await provider.ExecuteAsync(new(Capabilities.SymbolSearch), Token)).Error.Should().BeNull();
        (await refresher.RefreshAsync(new(true, ["A.cs"]), Token)).Skipped.Should().BeFalse();
        using var arguments = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(fixture.Root, "refresh.args"), Token));
        arguments.RootElement.GetProperty("full").GetBoolean().Should().BeTrue();
        arguments.RootElement.GetProperty("changed")[0].GetString().Should().Be("A.cs");
        await using var optedIn = Provider(fixture, configuration with { IndexWorktrees = true }, linked: true);
        (await optedIn.Should().BeAssignableTo<IProviderRefresher>().Which.RefreshAsync(new(), Token)).Skipped.Should().BeFalse();
    }

    [Fact]
    public async Task ArchitectureRelationsAndPlainStringListsRetainAllFields()
    {
        using var fixture = new ProviderFixture();
        fixture.Write("relations.json", """{"rows":[["A","src/A.cs",3,"B","src/B.cs",5,["A","B"],8,"0.9"]]}""");
        fixture.Write("files.json", """["src/A.cs","src/B.cs"]""");
        var mapping = new CapabilityMapping { Tool = "relations", Items = "rows", Fields = new(StringComparer.Ordinal) { ["name"] = "0", ["path"] = "1", ["start_line"] = "2", ["related_name"] = "3", ["related_path"] = "4", ["related_line"] = "5", ["members"] = "6", ["count"] = "7", ["confidence"] = "8" } };
        await using var provider = Provider(fixture, Configuration((Capabilities.ArchDependencies, mapping), (Capabilities.FileSearch, new() { Tool = "files" })));
        var relation = (await provider.ExecuteAsync(new(Capabilities.ArchDependencies), Token)).Items.Should().ContainSingle().Which;
        relation.Kind.Should().Be("relation");
        relation.Related.Should().Be(new ProviderReference("B", "src/B.cs", 5));
        relation.Members.Should().Equal("A", "B");
        relation.Count.Should().Be(8);
        relation.Confidence.Should().Be(0.9);
        (await provider.ExecuteAsync(new(Capabilities.FileSearch), Token)).Items.Select(item => item.Path).Should().Equal("src/A.cs", "src/B.cs");
    }

    [Fact]
    public async Task RealConfiguredCodebaseMemoryServerCompletesHandshakeAndListsToolsInIsolation()
    {
        var executable = Environment.GetEnvironmentVariable("HARNESS_REAL_CODEBASE_MEMORY");
        if (string.IsNullOrWhiteSpace(executable))
        {
            throw Xunit.Sdk.SkipException.ForSkip("Set HARNESS_REAL_CODEBASE_MEMORY to the installed codebase-memory-mcp executable for an isolated real-provider smoke test.");
        }

        using var fixture = new ProviderFixture();
        var config = Path.Combine(fixture.Root, "config.toml");
        await File.WriteAllTextAsync(config, "[providers.\"codebase-memory\"]\n", Token);
        var providerConfig = ConfigurationLoader.LoadFiles("", config).Value.Providers["codebase-memory"] with { Command = executable, Dir = "project", TimeoutMs = 10000 };
        var home = Directory.CreateDirectory(Path.Combine(fixture.Root, "home")).FullName;
        var environment = new Dictionary<string, string?> { ["HOME"] = home, ["USERPROFILE"] = home, ["LOCALAPPDATA"] = home, ["APPDATA"] = home, ["XDG_DATA_HOME"] = home, ["XDG_CONFIG_HOME"] = home, ["CODEBASE_MEMORY_DB"] = Path.Combine(home, "memory.db") };
        await using var client = await McpProcessClient.StartAsync(new ProviderProcessFactory(), new("codebase-memory", new(executable, []) { WorkingDirectory = fixture.Root, Environment = environment }), Token);
        var tools = await client.ListToolsAsync(Token);
        tools.Should().Contain(tool => tool.Name == "list_projects").And.Contain(tool => tool.Name == "search_graph");
        var projects = await client.CallToolAsync("list_projects", new Dictionary<string, object?>(), Token);
        projects.IsError.Should().NotBe(true);
        await using var provider = GenericMcpProvider.Build("codebase-memory", providerConfig, fixture.Root, new ProviderProcessFactory(), environment);
        provider.Capabilities().Should().Contain(info => info.Capability == Capabilities.SymbolSearch);
        var health = await provider.HealthAsync(true, Token);
        health.State.Should().Be("degraded");
        var receipt = Environment.GetEnvironmentVariable("HARNESS_PROVIDER_SMOKE_RECEIPT");
        if (!string.IsNullOrWhiteSpace(receipt))
        {
            await File.WriteAllTextAsync(receipt, JsonSerializer.Serialize(new { provider = client.ServerVersion, tools = tools.Count, state = health.State, isolated_home = true }), Token);
        }
    }
}
