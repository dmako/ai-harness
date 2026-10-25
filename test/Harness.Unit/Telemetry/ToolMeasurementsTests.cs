using System.Text.Json;
using AwesomeAssertions;
using Harness.Core.Telemetry;
using Harness.Mcp;
using ModelContextProtocol.Protocol;
using Xunit;

namespace Harness.Unit.Telemetry;

public sealed class ToolMeasurementsTests
{
    [Theory]
    [InlineData("""{"results":[],"errors":[]}""", true, "")]
    [InlineData("""{"results":[],"errors":["failed"]}""", false, "providers failed: failed")]
    [InlineData("""{"results":[1],"errors":["failed"]}""", false, "")]
    [InlineData("""{"message":"pong"}""", false, "")]
    public void MetricsInspectListsAndKeepPartialAnswersSuccessful(string json, bool empty, string error)
    {
        var result = new CallToolResult
        {
            StructuredContent = JsonSerializer.Deserialize<JsonElement>(json),
            Content = [new TextContentBlock { Text = "é😀" }],
        };
        var observation = ToolMeasurements.Measure(
            "search_project",
            null,
            "codex",
            DateTimeOffset.UtcNow,
            10,
            result,
            null
        );
        observation.Empty.Should().Be(empty);
        observation.Error.Should().Be(error);
        observation.Bytes.Should().Be(6);
    }

    [Theory]
    [InlineData(
        "search_project",
        """{"kind":"impact","scope":"workspace","depth":"deep"}""",
        "impact [workspace deep]"
    )]
    [InlineData("similar_concerns", """{}""", "[workspace light]")]
    [InlineData("search_project", """{"kind":"symbol"}""", "symbol")]
    public void KindsKeepWorkspaceDepth(string tool, string json, string kind)
    {
        var args = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json);
        ToolMeasurements.Measure(tool, args, "codex", DateTimeOffset.UtcNow, 0, null, null).Kind.Should().Be(kind);
    }

    [Fact]
    public void FailingOptionalAdapterCannotFailToolMeasurement()
    {
        Action action = () =>
            ToolMeasurements.Observe(new Broken(), new(DateTimeOffset.UtcNow, "ping", "", "codex", 1, 10, false));
        action.Should().NotThrow();
    }

    private sealed class Broken : IToolObserver
    {
        public void Observe(ToolObservation observation) => throw new IOException("optional adapter failed");
    }
}
