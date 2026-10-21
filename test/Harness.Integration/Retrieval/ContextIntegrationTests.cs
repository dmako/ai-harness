using System.Diagnostics.CodeAnalysis;
using System.Text;
using System.Text.Json;
using AwesomeAssertions;
using Harness.Core.Memory;
using Harness.Core.Providers;
using Harness.Infrastructure.Projects;
using Harness.Integration.Providers;
using Harness.Integration.Sync;
using Harness.Mcp.Context;
using Harness.Providers.Registry;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using Xunit;

namespace Harness.Integration.Retrieval;

[SuppressMessage("Reliability", "CA2000:Dispose objects before losing scope", Justification = "Fixture providers transfer ownership to the asynchronously disposed registry.")]
public sealed class ContextIntegrationTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task RepeatedContextHistoryRefreshesAfterACommitAndKeepsPathFiltersSeparate()
    {
        using var fixture = await SyncFixture.CreateAsync();
        var location = await fixture.Lifecycle.LocateAsync(fixture.Root, cancellationToken: Token);
        await using var session = new HarnessStateSession(location, fixture.Lifecycle.Registry, new("test", "", "test", "test"), fixture.Lifecycle.GlobalConfigurationPath);
        var context = await session.ReadContextProjectAsync(Token);
        var first = await context.Sources.RecentChanges!(["a.cs"], 5, Token);
        var repeated = await context.Sources.RecentChanges!(["a.cs"], 5, Token);
        repeated.Should().BeSameAs(first, "the same immutable commit/path history should avoid another Git subprocess");
        var other = await context.Sources.RecentChanges!(["b.cs"], 5, Token);
        other.SelectMany(c => c.Paths).Should().OnlyContain(p => p == "b.cs");
        fixture.Write("a.cs", "class Changed {}");
        await fixture.CommitAsync("new context history");
        var refreshed = await context.Sources.RecentChanges!(["a.cs"], 5, Token);
        refreshed[0].Subject.Should().Be("new context history");
        refreshed.Should().NotBeSameAs(first);
    }

    [Fact]
    public async Task InitializedContextIncludesBodiesMemoryChangesProposalsAndWithholdsCrossBranchCode()
    {
        using var fixture = await SyncFixture.CreateAsync();
        fixture.Write("a.cs", "class Alpha\n{\n    // é😀 <tag> & deterministic\n}\n");
        await fixture.CommitAsync("define Alpha");
        await fixture.RunAsync();
        var location = await fixture.Lifecycle.LocateAsync(fixture.Root, cancellationToken: Token);
        await using var session = new HarnessStateSession(location, fixture.Lifecycle.Registry, new("test", "", "test", "test"), fixture.Lifecycle.GlobalConfigurationPath);
        var decision = await session.PutMemoryAsync(new() { Type = "decision", Content = "Always use deterministic operations", Scope = new("path", "a.cs") }, Token);
        await session.ProposeMemoryAsync(new("warning", "Review the proposed operation"), Token);
        await using var registry = new ProviderRegistry();
        var switchDuringGather = false;
        registry.Register(new ProviderRegistryTests.Fake("index", [Capabilities.SymbolSearch], freshness: "indexed", execute: async (_, _) =>
        {
            if (switchDuringGather)
            { switchDuringGather = false; await fixture.GitAsync("switch", "-c", "other-branch"); }
            return new ProviderResult { Items = [new() { Name = "Alpha", Path = "a.cs", StartLine = 1, EndLine = 4, Provider = "index" }] };
        }));
        registry.Register(new ProviderRegistryTests.Fake("live", [Capabilities.SymbolSearch], execute: (_, _) => Task.FromResult(new ProviderResult { Items = [new() { Name = "Live", Path = "b.cs", Provider = "live" }] })));
        var tools = new ContextTools(registry, session);
        var first = await Call(tools, """{"task":"Alpha","debug":true}""");
        first.GetProperty("code")[0].GetProperty("units")[0].GetProperty("body").GetString().Should().Contain("é😀 <tag> & deterministic");
        first.GetProperty("memory").EnumerateArray().Should().Contain(memory => memory.GetProperty("id").GetString() == decision.Memory!.Id);
        first.GetProperty("changes").EnumerateArray().Should().Contain(change => change.GetProperty("subject").GetString() == "define Alpha");
        first.GetProperty("note").GetString().Should().Contain("1 memory proposal(s) wait for review");
        fixture.Write("a.cs", "class Alpha\n{\n    // fresh body\n}\n");
        var next = await Call(tools, """{"task":"Alpha"}""");
        next.GetProperty("code")[0].GetProperty("units")[0].GetProperty("body").GetString().Should().Contain("fresh body");
        switchDuringGather = true;
        var stale = await Call(tools, """{"task":"Alpha"}""");
        stale.GetProperty("code").EnumerateArray().Should().OnlyContain(file => file.GetProperty("path").GetString() != "a.cs");
        stale.GetProperty("stale").GetString().Should().Contain("withheld");
    }

    [Fact]
    public async Task UninitializedSessionIsReadOnlyAndDirectorySearchHitsAreDropped()
    {
        using var fixture = new ProviderFixture();
        Directory.CreateDirectory(Path.Combine(fixture.Root, "src"));
        var lifecycle = new ProjectLifecycle(Path.Combine(fixture.Root, "global", "config.toml"), "test");
        var location = await lifecycle.LocateAsync(fixture.Root, allowNonGit: true, cancellationToken: Token);
        await using var session = new HarnessStateSession(location, lifecycle.Registry, new("test", "", "test", "test"), lifecycle.GlobalConfigurationPath);
        await using var registry = new ProviderRegistry();
        registry.Register(new ProviderRegistryTests.Fake("files", [Capabilities.FileSearch], execute: (_, _) => Task.FromResult(new ProviderResult { Items = [new() { Path = "src", Provider = "files" }] })));
        var answer = await Call(new(registry, session), """{"task":"order templates","tokens":500,"budget":9000}""");
        answer.GetProperty("budget_bytes").GetInt32().Should().Be(2000);
        answer.GetProperty("code").GetArrayLength().Should().Be(0);
        answer.GetProperty("note").GetString().Should().Contain("no project memory");
        Directory.Exists(Path.Combine(fixture.Root, ".harness")).Should().BeFalse();
        File.Exists(Path.Combine(fixture.Root, "global", "projects.json")).Should().BeFalse();
    }

    [Fact]
    public async Task PublicMcpSchemaTextAndStructuredPayloadRespectTheSerializedBudget()
    {
        using var fixture = new ProviderFixture();
        var environment = StdioClientTransportOptions.GetDefaultEnvironmentVariables();
        environment["HARNESS_CONFIG_DIR"] = Path.Combine(fixture.Root, "global");
        var configuration = Directory.GetParent(ProviderFixture.Worker)!.Parent!.Name;
        var binary = Path.Combine(ProviderFixture.RepositoryRoot, "src", "Harness.Cli", "bin", configuration, "net10.0", "harness.dll");
        await using var client = await McpClient.CreateAsync(new StdioClientTransport(new() { Command = "dotnet", Arguments = [binary, "mcp", "--project", fixture.Root], WorkingDirectory = fixture.Root, EnvironmentVariables = environment, InheritEnvironmentVariables = false }), cancellationToken: Token);
        var tools = await client.ListToolsAsync(cancellationToken: Token);
        var tool = tools.Single(tool => tool.Name is "get_context").ProtocolTool;
        using var baseline = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(ProviderFixture.RepositoryRoot, "test", "fixtures", "compatibility", "go-windows-amd64.json"), Token));
        var schema = baseline.RootElement.GetProperty("cases").GetProperty("mcp").EnumerateArray().Single(entry => entry.GetProperty("name").GetString() == "tools_list")
            .GetProperty("response").GetProperty("result").GetProperty("tools").EnumerateArray().Single(entry => entry.GetProperty("name").GetString() == "get_context");
        var actual = JsonSerializer.SerializeToElement(tool);
        JsonElement.DeepEquals(actual.GetProperty("inputSchema"), schema.GetProperty("inputSchema")).Should().BeTrue();
        JsonElement.DeepEquals(actual.GetProperty("outputSchema"), schema.GetProperty("outputSchema")).Should().BeTrue();
        var result = await client.CallToolAsync("get_context", new Dictionary<string, object?> { ["task"] = "fix é😀 <tag> & escaped \"text\"", ["budget"] = 120, ["debug"] = true }, cancellationToken: Token);
        result.IsError.Should().BeNull();
        var text = result.Content.Should().ContainSingle().Which.Should().BeOfType<TextContentBlock>().Which.Text;
        var size = Encoding.UTF8.GetByteCount(text);
        size.Should().BeLessThanOrEqualTo(1000);
        result.StructuredContent!.Value.GetProperty("used_bytes").GetInt32().Should().Be(size);
        using var parsed = JsonDocument.Parse(text);
        JsonElement.DeepEquals(parsed.RootElement, result.StructuredContent.Value).Should().BeTrue();
        foreach (var args in new[] { new Dictionary<string, object?>(), new() { ["task"] = " " }, new() { ["task"] = "x", ["path"] = "../outside" }, new() { ["task"] = "x", ["scope"] = "invalid" }, new() { ["task"] = "x", ["depth"] = "invalid" }, new() { ["task"] = "x", ["budget"] = "oops" }, new() { ["task"] = "x", ["tokens"] = int.MaxValue } })
        {
            (await client.CallToolAsync("get_context", args, cancellationToken: Token)).IsError.Should().BeTrue();
        }

        Directory.Exists(Path.Combine(fixture.Root, ".harness")).Should().BeFalse();
    }

    private static async Task<JsonElement> Call(ContextTools tools, string json)
    {
        var answer = await tools.CallAsync(JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json), Token);
        using var parsed = JsonDocument.Parse(answer.Json);
        parsed.RootElement.GetProperty("used_bytes").GetInt32().Should().Be(Encoding.UTF8.GetByteCount(answer.Json));
        Encoding.UTF8.GetByteCount(answer.Json).Should().BeLessThanOrEqualTo(parsed.RootElement.GetProperty("budget_bytes").GetInt32());
        return parsed.RootElement.Clone();
    }
}
