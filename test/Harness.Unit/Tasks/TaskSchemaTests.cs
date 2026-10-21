using System.Text.Json;
using AwesomeAssertions;
using Harness.Mcp.Memory;
using ModelContextProtocol.Protocol;
using Xunit;

namespace Harness.Unit.Tasks;

public sealed class TaskSchemaTests
{
    [Theory]
    [InlineData("""{"validation":["build"]}""", true)]
    [InlineData("""{"validation":[{"command":"build","result":"pass"}]}""", true)]
    [InlineData("""{"validation":null,"acceptance":null,"proposals":null}""", true)]
    [InlineData("""{"validation":[{}]}""", false)]
    [InlineData("""{"validation":[true]}""", false)]
    [InlineData("""{"validation":[{"command":"build","result":"pass","extra":1}]}""", false)]
    [InlineData("""{"acceptance":[{"index":1}]}""", false)]
    [InlineData("""{"acceptance":[{"index":1,"result":"pass","extra":1}]}""", false)]
    [InlineData("""{"proposals":[{"type":"decision"}]}""", false)]
    [InlineData("""{"proposals":[{"type":"decision","content":"text","extra":1}]}""", false)]
    public void CompletionSchemaValidatesEveryNestedMemberAndUnion(string json, bool valid)
    {
        var assembly = typeof(Harness.Mcp.HarnessMcpServer).Assembly;
        using var stream = assembly.GetManifestResourceStream("Harness.Mcp.Schemas.task_complete.json");
        stream.Should().NotBeNull();
        var tool = JsonSerializer.Deserialize<Tool>(stream, ModelContextProtocol.McpJsonUtilities.DefaultOptions)!;
        var arguments = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json)!;
        foreach (var key in new[] { "id", "claim_token", "summary" })
            arguments[key] = JsonSerializer.SerializeToElement("value");
        (MemoryArguments.Validate(tool, arguments) is null).Should().Be(valid);
    }
}
