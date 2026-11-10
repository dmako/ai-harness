using System.Text.Json.Nodes;
using Harness.Core.Agents;
using Harness.Core.Providers;
using Harness.Core.Tasks;

namespace Harness.Agents;

/// <summary>How a client is started: its executable and the arguments that come before harness's own.</summary>
public sealed record AgentCommand(string Executable, IReadOnlyList<string> Prefix)
{
    public static AgentCommand Of(string executable) => new(executable, []);
}

/// <summary>
/// Runs Claude Code in print mode with stream-json output: a session id harness chooses (--session-id) or resumes
/// (--resume), the prompt on standard input, and HARNESS_TASK_ID in its environment, which the harness MCP server it
/// starts inherits.
/// </summary>
public sealed class ClaudeRunner(IProviderProcessFactory processes, AgentCommand? command = null) : IAgentRunner
{
    public const string TaskVariable = "HARNESS_TASK_ID";

    private readonly AgentCommand _command = command ?? AgentCommand.Of("claude");

    public string Client => AgentClients.Claude;

    public Task<IAgentRun> StartAsync(AgentStart start, CancellationToken cancellationToken = default) =>
        RunAsync(start, "--session-id", Guid.NewGuid().ToString(), cancellationToken);

    public Task<IAgentRun> ResumeAsync(
        string sessionId,
        AgentStart start,
        CancellationToken cancellationToken = default
    ) => RunAsync(start, "--resume", sessionId, cancellationToken);

    private Task<IAgentRun> RunAsync(
        AgentStart start,
        string option,
        string sessionId,
        CancellationToken cancellationToken
    )
    {
        ArgumentNullException.ThrowIfNull(start);
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        List<string> arguments =
        [
            .. _command.Prefix,
            "-p",
            "--output-format",
            "stream-json",
            "--verbose",
            option,
            sessionId,
        ];
        if (start.Model.Length > 0)
        {
            arguments.AddRange(["--model", start.Model]);
        }
        arguments.AddRange(start.Arguments);
        var options = new ProviderProcessOptions(_command.Executable, arguments)
        {
            WorkingDirectory = start.WorkingDirectory,
            Environment = AgentEnvironment.For(start),
        };
        return AgentProcessRun.StartAsync(
            processes,
            options,
            Client,
            start.TaskId,
            start.Prompt,
            new ClaudeParser(sessionId),
            cancellationToken
        );
    }
}

internal static class AgentEnvironment
{
    public static Dictionary<string, string?> For(AgentStart start)
    {
        Dictionary<string, string?> environment = new(StringComparer.Ordinal);
        foreach (var (key, value) in start.Environment)
        {
            environment[key] = value;
        }
        environment[ClaudeRunner.TaskVariable] = start.TaskId;
        return environment;
    }
}

/// <summary>
/// Claude Code's stream-json: system/init names the session and model, assistant messages carry text and tool_use
/// blocks, and result ends the turn with its usage, API-equivalent cost and duration.
/// </summary>
internal sealed class ClaudeParser : AgentParser
{
    public ClaudeParser(string sessionId) => SessionId = sessionId;

    public override void Read(JsonObject line, DateTimeOffset at, List<AgentEvent> events)
    {
        if (Text(line["session_id"]) is { Length: > 0 } session)
        {
            SessionId = session;
        }
        switch (Text(line["type"]))
        {
            case "system" when Text(line["subtype"]) is "init":
                Model = Text(line["model"]);
                events.Add(new(AgentEventKind.Session, at) { SessionId = SessionId, Model = Model });
                break;
            case "assistant":
                Assistant(line["message"] as JsonObject, at, events);
                break;
            case "result":
                Result(line, at, events);
                break;
            default:
                break;
        }
    }

    private void Assistant(JsonObject? message, DateTimeOffset at, List<AgentEvent> events)
    {
        if (message is null)
        {
            return;
        }
        if (Text(message["model"]) is { Length: > 0 } model && !model.StartsWith('<', StringComparison.Ordinal))
        {
            Model = model;
        }
        foreach (var block in message["content"] as JsonArray ?? [])
        {
            switch (Text(block?["type"]))
            {
                case "text" when Text(block?["text"]) is { Length: > 0 } text:
                    events.Add(new(AgentEventKind.Text, at) { SessionId = SessionId, Text = text });
                    break;
                case "tool_use":
                    events.Add(
                        new(AgentEventKind.ToolCall, at)
                        {
                            SessionId = SessionId,
                            Tool = Text(block?["name"]),
                            Text = Short(Input(block?["input"] as JsonObject)),
                        }
                    );
                    break;
                default:
                    break;
            }
        }
    }

    /// <summary>The input that says most about a tool call: a command, a path or a pattern, else all of it.</summary>
    private static string Input(JsonObject? input)
    {
        if (input is null)
        {
            return "";
        }
        foreach (var key in (string[])["command", "file_path", "path", "pattern", "url", "query", "description"])
        {
            if (Text(input[key]) is { Length: > 0 } text)
            {
                return text;
            }
        }
        return input.ToJsonString();
    }

    private void Result(JsonObject line, DateTimeOffset at, List<AgentEvent> events)
    {
        var usage = line["usage"] as JsonObject;
        var tokens = new TokenCounts(
            Number(usage?["input_tokens"]),
            Number(usage?["cache_read_input_tokens"]),
            Number(usage?["cache_creation_input_tokens"]),
            Number(usage?["output_tokens"])
        );
        events.Add(
            new(AgentEventKind.Usage, at)
            {
                SessionId = SessionId,
                Model = Model,
                Usage = tokens,
            }
        );
        var failed = line["is_error"] is JsonValue error && error.TryGetValue<bool>(out var isError) && isError;
        failed |= Text(line["subtype"]) is { Length: > 0 } subtype && subtype is not "success";
        var text = Text(line["result"]);
        double? cost =
            line["total_cost_usd"] is JsonValue value && value.TryGetValue<double>(out var dollars) ? dollars : null;
        Finished = new(failed ? AgentRunStatus.Failed : AgentRunStatus.Completed, SessionId)
        {
            Text = failed ? "" : text,
            Error = failed ? Errors(line, text) : "",
            Model = Model,
            Usage = tokens,
            CostUsd = cost,
            DurationMs = Number(line["duration_ms"]),
        };
    }

    private static string Errors(JsonObject line, string text)
    {
        var errors = (line["errors"] as JsonArray ?? []).Select(e => Text(e)).Where(e => e.Length > 0).ToList();
        if (errors.Count > 0)
        {
            return string.Join("; ", errors);
        }
        return text.Length > 0 ? text : Text(line["subtype"]);
    }
}
