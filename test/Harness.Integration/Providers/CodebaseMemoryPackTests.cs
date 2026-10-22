using System.Text.Json;
using AwesomeAssertions;
using Harness.Core.Providers;
using Harness.Infrastructure.Configuration;
using Harness.Infrastructure.Processes;
using Harness.Providers.Mcp;
using Xunit;

namespace Harness.Integration.Providers;

[Collection("Provider processes")]
public sealed class CodebaseMemoryPackTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData("23-60", 0, 23, 60, "", "symbol", "WriteAtomic")]
    [InlineData(" 23 - +60 ", 0, 23, 60, "", "symbol", "WriteAtomic")]
    [InlineData("23-60", 80, 23, 80, "", "symbol", "WriteAtomic")]
    [InlineData("23-invalid", 80, 0, 80, "", "symbol", "WriteAtomic")]
    [InlineData("1-2-3", 0, 0, 0, "", "symbol", "WriteAtomic")]
    [InlineData("23", 0, 23, 0, "", "symbol", "WriteAtomic")]
    [InlineData("23-60", 0, 23, 60, "explicit", "symbol", "explicit")]
    [InlineData("23-60", 0, 23, 60, "", "file", "file.cs")]
    public async Task GenericRowsPreserveExplicitFieldsAndHandleLineRanges(
        string line,
        int endLine,
        int expectedStart,
        int expectedEnd,
        string name,
        string kind,
        string expectedName
    )
    {
        using var fixture = new ProviderFixture();
        fixture.Write(
            "answer.json",
            JsonSerializer.Serialize(
                new[]
                {
                    new
                    {
                        line,
                        endLine,
                        name,
                        qualifiedName = "proj.internal.fsutil.WriteAtomic",
                        path = "src/file.cs",
                    },
                }
            )
        );
        var configuration = new ProviderConfiguration
        {
            Type = "mcp",
            Command = "dotnet",
            Args = [ProviderFixture.Worker, "normal", fixture.Root],
            Capabilities = new(StringComparer.Ordinal)
            {
                [Capabilities.SymbolSearch] = new()
                {
                    Tool = "answer",
                    Kind = kind,
                    Fields = new(StringComparer.Ordinal)
                    {
                        ["name"] = "name",
                        ["qualified_name"] = "qualifiedName",
                        ["path"] = "path",
                        ["start_line"] = "line",
                        ["end_line"] = "endLine",
                    },
                },
            },
        };
        await using var provider = GenericMcpProvider.Build(
            "fixture",
            configuration,
            fixture.Root,
            new ProviderProcessFactory()
        );
        var result = await provider.ExecuteAsync(new(Capabilities.SymbolSearch), Token);
        result.Error.Should().BeNull();
        var item = result.Items.Should().ContainSingle().Which;
        item.Name.Should().Be(expectedName);
        item.StartLine.Should().Be(expectedStart);
        item.EndLine.Should().Be(expectedEnd);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task EmbeddedPackRequestsJsonAndMapsModernRowsOrLegacyResults(bool legacy)
    {
        using var fixture = new ProviderFixture();
        fixture.Write("config.toml", "[providers.codebase-memory]\n");
        var configuration = ConfigurationLoader
            .LoadFiles("", Path.Combine(fixture.Root, "config.toml"))
            .Value.Providers["codebase-memory"];
        configuration.Only.Should().NotContain(Capabilities.TextSearch);
        fixture.Write(
            "list_projects.json",
            JsonSerializer.Serialize(
                new { projects = new[] { new { root_path = fixture.Root, name = "mapped-project" } } }
            )
        );
        fixture.Write(
            "search_graph.json",
            legacy
                ? """
                {"results":[{"name":"WriteAtomic","qualified_name":"proj.internal.fsutil.WriteAtomic",
                "label":"Function","file_path":"internal/fsutil/fsutil.go","start_line":23,"end_line":60,"rank":-49.5}],"has_more":true}
                """
                : """
                {"cols":["qn","label","file","lines","rank"],
                "rows":[["proj.internal.fsutil.WriteAtomic","Function","internal/fsutil/fsutil.go","23-60",-49.5]],"has_more":true}
                """
        );
        fixture.Write(
            "search_code.json",
            legacy
                ? """
                {"results":[{"node":"WriteAtomic","qualified_name":"proj.internal.fsutil.WriteAtomic",
                "label":"Function","file":"internal/fsutil/fsutil.go","match_lines":[34,40],"start_line":23}]}
                """
                : """
                {"rows":[["proj.internal.fsutil.WriteAtomic","Function","internal/fsutil/fsutil.go","23-60",[34,40]]]}
                """
        );
        fixture.Write("get_architecture.json", """{"modules":2}""");
        fixture.Write(
            "query_graph.json",
            """{"rows":[["WriteAtomic","proj.WriteAtomic","file.go",23,60,"Other","other.go",0.9]]}"""
        );
        await using var provider = GenericMcpProvider.Build(
            "codebase-memory",
            configuration with
            {
                Command = "dotnet",
                Args = [ProviderFixture.Worker, "normal", fixture.Root],
                TimeoutMs = 10000,
            },
            fixture.Root,
            new ProviderProcessFactory()
        );

        var symbols = await provider.ExecuteAsync(new(Capabilities.SymbolSearch, "WriteAtomic", Limit: 5), Token);
        symbols.Error.Should().BeNull();
        symbols.Truncated.Should().BeTrue();
        symbols
            .Items.Should()
            .ContainSingle()
            .Which.Should()
            .BeEquivalentTo(
                new ProviderItem
                {
                    Kind = "symbol",
                    Name = "WriteAtomic",
                    QualifiedName = "proj.internal.fsutil.WriteAtomic",
                    Label = "Function",
                    Path = "internal/fsutil/fsutil.go",
                    StartLine = 23,
                    EndLine = 60,
                    Score = -49.5,
                    Provider = "codebase-memory",
                    Tool = "search_graph",
                }
            );
        var text = await provider.ExecuteAsync(new(Capabilities.TextSearch, "WriteAtomic"), Token);
        text.Error.Should().BeNull();
        var match = text.Items.Should().ContainSingle().Which;
        match.Name.Should().Be("WriteAtomic");
        match.StartLine.Should().Be(34);
        match.EndLine.Should().Be(0);
        match.Snippet.Should().Be("[34,40]");
        (await provider.ExecuteAsync(new(Capabilities.Overview), Token)).Error.Should().BeNull();
        (await provider.ExecuteAsync(new(Capabilities.ArchDuplicates), Token)).Items.Should().HaveCount(2);
        foreach (
            var tool in new[] { "list_projects", "search_graph", "search_code", "get_architecture", "query_graph" }
        )
        {
            using var arguments = JsonDocument.Parse(
                await File.ReadAllTextAsync(Path.Combine(fixture.Root, tool + ".args"), Token)
            );
            arguments.RootElement.GetProperty("format").GetString().Should().Be("json", tool);
            if (tool is not "list_projects")
            {
                arguments.RootElement.GetProperty("project").GetString().Should().Be("mapped-project", tool);
            }
        }
    }
}
