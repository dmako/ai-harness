using System.Text.Json.Nodes;
using Harness.Core.Agents;
using Harness.Core.Providers;
using Harness.Core.Tasks;

namespace Harness.Agents;

/// <summary>
/// Runs Codex with `codex exec --json` (one turn per process), the prompt on standard input: a new thread, whose id
/// arrives with thread.started, or `codex exec resume --json &lt;thread&gt;`. The run ends at turn.completed or
/// turn.failed, not when the process exits.
/// </summary>
public sealed class CodexRunner(IProviderProcessFactory processes, AgentCommand? command = null) : IAgentRunner
{
    private readonly AgentCommand _command = command ?? AgentCommand.Of("codex");

    public string Client => AgentClients.Codex;

    public Task<IAgentRun> StartAsync(AgentStart start, CancellationToken cancellationToken = default) =>
        RunAsync(start, ["exec", "--json"], "", cancellationToken);

    public Task<IAgentRun> ResumeAsync(
        string sessionId,
        AgentStart start,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        return RunAsync(start, ["exec", "resume", "--json"], sessionId, cancellationToken);
    }

    private Task<IAgentRun> RunAsync(
        AgentStart start,
        string[] verb,
        string sessionId,
        CancellationToken cancellationToken
    )
    {
        ArgumentNullException.ThrowIfNull(start);
        List<string> arguments = [.. _command.Prefix, .. verb];
        if (start.Model.Length > 0)
        {
            arguments.AddRange(["--model", start.Model]);
        }
        arguments.AddRange(start.Arguments);
        if (sessionId.Length > 0)
        {
            arguments.Add(sessionId);
        }
        arguments.Add("-");
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
            new CodexParser(sessionId, start.Model),
            cancellationToken
        );
    }
}

/// <summary>
/// codex exec's JSON lines: thread.started names the thread, item.completed carries messages and tool calls,
/// turn.completed ends the turn with its usage (input counts cached input too) and turn.failed with an error.
/// </summary>
internal sealed class CodexParser : AgentParser
{
    private string _text = "";
    private string _error = "";

    public CodexParser(string sessionId, string model)
    {
        SessionId = sessionId;
        Model = model;
    }

    public override void Read(JsonObject line, DateTimeOffset at, List<AgentEvent> events)
    {
        switch (Text(line["type"]))
        {
            case "thread.started":
                SessionId = Text(line["thread_id"]) is { Length: > 0 } thread ? thread : SessionId;
                events.Add(new(AgentEventKind.Session, at) { SessionId = SessionId, Model = Model });
                break;
            case "item.completed":
                Item(line["item"] as JsonObject, at, events);
                break;
            case "turn.completed":
                Completed(line["usage"] as JsonObject, at, events);
                break;
            case "turn.failed":
                Finished = new(AgentRunStatus.Failed, SessionId)
                {
                    Error = Text(line["error"]?["message"]) is { Length: > 0 } message ? message : _error,
                    Model = Model,
                };
                break;
            case "error":
                _error = Text(line["message"]);
                break;
            default:
                break;
        }
    }

    private void Item(JsonObject? item, DateTimeOffset at, List<AgentEvent> events)
    {
        var (tool, text) = Text(item?["type"]) switch
        {
            "agent_message" => ("", Text(item?["text"])),
            "command_execution" => ("shell", Text(item?["command"])),
            "mcp_tool_call" => (Text(item?["server"]) + "." + Text(item?["tool"]), ""),
            "file_change" => ("apply_patch", Paths(item?["changes"] as JsonArray)),
            "web_search" => ("web_search", Text(item?["query"])),
            _ => ("", ""),
        };
        if (tool.Length > 0)
        {
            events.Add(
                new(AgentEventKind.ToolCall, at)
                {
                    SessionId = SessionId,
                    Tool = tool,
                    Text = Short(text),
                }
            );
        }
        else if (text.Length > 0)
        {
            _text = text;
            events.Add(new(AgentEventKind.Text, at) { SessionId = SessionId, Text = text });
        }
    }

    private static string Paths(JsonArray? changes) =>
        string.Join(", ", (changes ?? []).Select(change => Text(change?["path"])).Where(path => path.Length > 0));

    private void Completed(JsonObject? usage, DateTimeOffset at, List<AgentEvent> events)
    {
        var input = Number(usage?["input_tokens"]);
        var cached = Number(usage?["cached_input_tokens"]);
        var written = Number(usage?["cache_write_input_tokens"]);
        // As in the rollouts: input includes what was read from or written to the cache, output includes reasoning.
        var tokens = new TokenCounts(
            Math.Max(0, input - cached - written),
            cached,
            written,
            Number(usage?["output_tokens"]),
            Number(usage?["reasoning_output_tokens"])
        );
        events.Add(
            new(AgentEventKind.Usage, at)
            {
                SessionId = SessionId,
                Model = Model,
                Usage = tokens,
            }
        );
        Finished = new(AgentRunStatus.Completed, SessionId)
        {
            Text = _text,
            Model = Model,
            Usage = tokens,
        };
    }
}
