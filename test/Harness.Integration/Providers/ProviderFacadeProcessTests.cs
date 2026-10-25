using System.Text.Json;
using AwesomeAssertions;
using Harness.Core.Providers;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using Xunit;

namespace Harness.Integration.Providers;

[Collection("Provider processes")]
public sealed class ProviderFacadeProcessTests
{
    [Fact]
    public async Task CliAdvertisesSchemasAndRoutesFacadeToolsWithoutInitializingState()
    {
        var token = TestContext.Current.CancellationToken;
        using var fixture = new ProviderFixture();
        fixture.Write("harness_provider_info.json", ProtocolProviderTests.Info);
        fixture.Write("code_symbol_search.json", ProtocolProviderTests.Answer);
        fixture.Write("package_info.json", """{"items":[],"text":"public API"}""");
        var state = Directory.CreateDirectory(Path.Combine(fixture.Root, ".harness")).FullName;
        var configuration =
            "[providers.analyzer]\ntype = \"harness\"\ncommand = \"dotnet\"\nargs = "
            + JsonSerializer.Serialize(new[] { ProviderFixture.Worker, "normal", fixture.Root })
            + "\ndir = \"project\"\n";
        await File.WriteAllTextAsync(Path.Combine(state, "config.toml"), configuration, token);
        var environment = StdioClientTransportOptions.GetDefaultEnvironmentVariables();
        environment["HARNESS_CONFIG_DIR"] = Path.Combine(fixture.Root, "global");
        var workerDirectory = Directory.GetParent(ProviderFixture.Worker)!.Parent!.Name;
        var binary = Path.Combine(
            ProviderFixture.RepositoryRoot,
            "src",
            "Harness.Cli",
            "bin",
            workerDirectory,
            "net10.0",
            "harness.dll"
        );
        var options = new StdioClientTransportOptions
        {
            Command = "dotnet",
            Arguments = [binary, "mcp", "--project", fixture.Root],
            WorkingDirectory = fixture.Root,
            EnvironmentVariables = environment,
            InheritEnvironmentVariables = false,
            ShutdownTimeout = TimeSpan.FromSeconds(10),
        };
        await using (
            var client = await McpClient.CreateAsync(new StdioClientTransport(options), cancellationToken: token)
        )
        {
            var tools = await client.ListToolsAsync(cancellationToken: token);
            foreach (var name in new[] { "search_project", "provider_status", "package_api", "project_overview" })
            {
                ToolSchema.AssertMatches(tools.Single(tool => tool.Name == name));
            }
            var search = await client.CallToolAsync(
                "search_project",
                new Dictionary<string, object?>
                {
                    ["query"] = "Alpha",
                    ["kind"] = "symbol",
                    ["limit"] = 1,
                },
                cancellationToken: token
            );
            search.IsError.Should().BeNull();
            search
                .StructuredContent!.Value.GetProperty("results")[0]
                .GetProperty("provider")
                .GetString()
                .Should()
                .Be("analyzer");
            var status = await client.CallToolAsync("provider_status", cancellationToken: token);
            status
                .StructuredContent!.Value.GetProperty("providers")[0]
                .GetProperty("state")
                .GetString()
                .Should()
                .Be("ready");
            var overview = await client.CallToolAsync("project_overview", cancellationToken: token);
            overview.StructuredContent!.Value.GetProperty("project_root").GetString().Should().Be(fixture.Root);
            var package = await client.CallToolAsync(
                "package_api",
                new Dictionary<string, object?> { ["action"] = "info", ["package"] = "Foo" },
                cancellationToken: token
            );
            package
                .StructuredContent!.Value.GetProperty("answers")[0]
                .GetProperty("text")
                .GetString()
                .Should()
                .Be("public API");
            var invalid = await client.CallToolAsync(
                "search_project",
                new Dictionary<string, object?> { ["query"] = "Alpha", ["path"] = 2 },
                cancellationToken: token
            );
            invalid.IsError.Should().BeTrue();
        }
        File.Exists(Path.Combine(state, "harness.db")).Should().BeFalse();
        File.Exists(Path.Combine(fixture.Root, "global", "projects.json")).Should().BeFalse();
    }
}
