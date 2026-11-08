using System.Text;
using System.Text.Json;
using AwesomeAssertions;
using Harness.Core.Hooks;
using Xunit;

namespace Harness.Unit.Hooks;

/// <summary>
/// The task id a hook event keeps from harness's task tools (TASK-0113), in the shapes Claude Code and Codex send
/// PostToolUse: Claude Code's MCP answer as text content blocks, Codex's as the call result with structuredContent.
/// </summary>
public sealed class TaskLinkTests
{
    private const string Answer = """
        {"claim_token":"clm_SECRET_TOKEN","task":{"id":"TASK-0007","title":"Private title","goal":"PRIVATE_GOAL"}}
        """;

    private static string ClaudeResponse(string answer) =>
        JsonSerializer.Serialize(new object[] { new { type = "text", text = answer } });

    private static string CodexResponse(string answer) =>
        $$"""{"content":[{"type":"text","text":{{JsonSerializer.Serialize(answer)}}}],"structuredContent":{{answer}}}""";

    private static HookCall Parse(string tool, string input, string response) =>
        HookPayload.Parse(
            Encoding.UTF8.GetBytes(
                $$"""
                {"session_id":"s-1","cwd":"/repo","hook_event_name":"PostToolUse","tool_name":"{{tool}}",
                 "tool_input":{{input}},"tool_response":{{response}}}
                """
            )
        );

    [Theory]
    [InlineData("claude", "task_claim", """{"id":"TASK-0007","lease_minutes":60}""", "claim")]
    [InlineData("codex", "task_claim", """{"id":"TASK-0007"}""", "claim")]
    [InlineData("claude", "task_next", """{"blueprint":"BP-006","claim":true}""", "claim")]
    [InlineData("codex", "task_next", """{"claim":true}""", "claim")]
    [InlineData(
        "claude",
        "task_update",
        """{"id":"TASK-0007","claim_token":"clm_X","status":"in_progress"}""",
        "update"
    )]
    [InlineData("codex", "task_update", """{"id":"TASK-0007","claim_token":"clm_X","note":"PRIVATE"}""", "update")]
    [InlineData("claude", "task_update", """{"id":"TASK-0007","claim_token":"clm_X","status":"blocked"}""", "block")]
    [InlineData("codex", "task_update", """{"id":"TASK-0007","claim_token":"clm_X","status":"ready"}""", "release")]
    [InlineData(
        "claude",
        "task_complete",
        """{"id":"TASK-0007","claim_token":"clm_X","summary":"PRIVATE"}""",
        "complete"
    )]
    [InlineData(
        "codex",
        "task_complete",
        """{"id":"TASK-0007","claim_token":"clm_X","summary":"PRIVATE"}""",
        "complete"
    )]
    public void HarnessTaskCallsKeepOnlyTheTaskIdAndWhatTheCallDid(
        string client,
        string tool,
        string input,
        string action
    )
    {
        var call = Parse(
            "mcp__harness__" + tool,
            input,
            client is "claude" ? ClaudeResponse(Answer) : CodexResponse(Answer)
        );
        (call.TaskId, call.TaskAction).Should().Be(("TASK-0007", action));
        (call.McpServer, call.McpTool, call.Kind).Should().Be(("harness", tool, "mcp"));
        var kept = JsonSerializer.Serialize(
            new[] { call.Session, call.Cwd, call.Tool, call.Kind, call.McpServer, call.McpTool, call.Task, call.TaskId }
        );
        kept.Should().NotContain("PRIVATE").And.NotContain("clm_").And.NotContain("BP-006");
    }

    [Fact]
    public void WithoutAReadableAnswerTheIdArgumentIsKeptButTaskNextHasNone()
    {
        Parse("mcp__harness__task_claim", """{"id":"TASK-0009"}""", "null").TaskId.Should().Be("TASK-0009");
        Parse("mcp__harness__task_update", """{"id":"TASK-0009"}""", "\"done\"").TaskId.Should().Be("TASK-0009");
        Parse("mcp__harness__task_next", """{"claim":true}""", "null").TaskId.Should().BeEmpty();
    }

    [Theory]
    [InlineData("mcp__harness__task_next", """{"blueprint":"BP-006"}""", "an unclaimed peek")]
    [InlineData("mcp__harness__task_get", """{"id":"TASK-0007"}""", "a read")]
    [InlineData("mcp__other__task_claim", """{"id":"TASK-0007"}""", "another server")]
    [InlineData("mcp__harness__get_context", """{"task":"TASK-0007"}""", "another tool")]
    public void OtherCallsLinkNoTask(string tool, string input, string because)
    {
        var call = Parse(tool, input, ClaudeResponse(Answer));
        (call.TaskId, call.TaskAction).Should().Be(("", ""), because);
    }

    [Theory]
    [InlineData("""{"isError":true,"content":[{"type":"text","text":"task TASK-0007 is not ready"}]}""")]
    [InlineData("""{"Err":"tool call failed"}""")]
    [InlineData(
        """
            "{\"isError\":true}"
            """
    )]
    public void FailedCallsLinkNoTask(string response) =>
        Parse("mcp__harness__task_claim", """{"id":"TASK-0007"}""", response).TaskId.Should().BeEmpty();
}
