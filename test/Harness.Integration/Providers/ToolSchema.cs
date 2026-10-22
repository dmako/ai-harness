using System.Text.Json;
using AwesomeAssertions;
using ModelContextProtocol.Client;

namespace Harness.Integration.Providers;

internal static class ToolSchema
{
    public static void AssertMatches(McpClientTool tool)
    {
        using var schema = JsonDocument.Parse(
            File.ReadAllText(
                Path.Combine(ProviderFixture.RepositoryRoot, "src", "Harness.Mcp", "Schemas", tool.Name + ".json")
            )
        );
        var actual = JsonSerializer.SerializeToElement(tool.ProtocolTool);
        foreach (var property in new[] { "inputSchema", "outputSchema" })
        {
            JsonElement
                .DeepEquals(schema.RootElement.GetProperty(property), actual.GetProperty(property))
                .Should()
                .BeTrue(tool.Name + " " + property);
        }
    }
}
