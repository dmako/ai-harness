using System.Text.Json;
using AwesomeAssertions;
using Harness.Integration.Providers;
using Harness.Integration.Sync;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using Xunit;

namespace Harness.Integration.Workspaces;

[Collection("Provider processes")]
public sealed class CouplingProcessTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task PublicChangeCouplingPreservesSchemaPrecedenceDirtyPathsAndUnknownProviderNotes()
    {
        using var fixture = await SyncFixture.CreateAsync();
        fixture.Write("a.cs", "uncommitted");
        var binary = Path.Combine(
            ProviderFixture.RepositoryRoot,
            "src",
            "Harness.Cli",
            "bin",
            Directory.GetParent(ProviderFixture.Worker)!.Parent!.Name,
            "net10.0",
            "harness.dll"
        );
        var environment = StdioClientTransportOptions.GetDefaultEnvironmentVariables();
        environment["HARNESS_CONFIG_DIR"] = fixture.Global;
        await using var client = await McpClient.CreateAsync(
            new StdioClientTransport(
                new()
                {
                    Command = "dotnet",
                    Arguments = [binary, "mcp", "--project", fixture.Root],
                    WorkingDirectory = fixture.Root,
                    EnvironmentVariables = environment,
                    InheritEnvironmentVariables = false,
                }
            ),
            cancellationToken: Token
        );
        var tools = await client.ListToolsAsync(cancellationToken: Token);
        tools.Should().HaveCount(30);
        ToolSchema.AssertMatches(tools.Single(tool => tool.Name == "change_coupling"));
        var file = await CallAsync(new() { ["file"] = "./a.cs", ["check"] = true });
        file.TryGetProperty("changed", out _).Should().BeFalse();
        file.GetProperty("note").GetString().Should().Contain("partners are unknown");
        var check = await CallAsync(new() { ["check"] = true });
        check.GetProperty("changed").EnumerateArray().Select(value => value.GetString()).Should().Equal("a.cs");
        check.GetProperty("note").GetString().Should().Contain("no usual partner is left out");
        var explicitFiles = await CallAsync(new() { ["check"] = true, ["files"] = new[] { "b.cs" } });
        explicitFiles.GetProperty("changed")[0].GetString().Should().Be("b.cs");
        var hotspots = await CallAsync(new() { ["limit"] = 0 });
        hotspots.GetProperty("commits").GetInt32().Should().Be(1);
        hotspots.TryGetProperty("hotspots", out _).Should().BeFalse();
        var invalid = await client.CallToolAsync(
            "change_coupling",
            new Dictionary<string, object?> { ["unknown"] = "value" },
            cancellationToken: Token
        );
        invalid.IsError.Should().BeTrue();

        async Task<JsonElement> CallAsync(Dictionary<string, object?> arguments)
        {
            var result = await client.CallToolAsync("change_coupling", arguments, cancellationToken: Token);
            result.IsError.Should().BeNull();
            var structured = result.StructuredContent!.Value;
            using var text = JsonDocument.Parse(result.Content.OfType<TextContentBlock>().Single().Text);
            JsonElement.DeepEquals(structured, text.RootElement).Should().BeTrue();
            return structured;
        }
    }
}
