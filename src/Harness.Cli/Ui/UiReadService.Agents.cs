using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Harness.Core.Agents;
using Harness.Core.Serialization;
using Harness.Core.Web;
using Harness.Infrastructure.Tasks;

namespace Harness.Cli.Ui;

internal sealed partial class UiReadService
{
    private const int TailBytes = 256 * 1024;
    private const int TailEntries = 60;

    private UiAgents Agents => agents ?? throw new UiRequestException(503, "agent sessions are not available here");

    /// <summary>How to go on with a session in a terminal.</summary>
    internal static string ResumeCommand(string client, string sessionId) =>
        client is AgentClients.Codex ? "codex resume " + sessionId : "claude --resume " + sessionId;

    /// <summary>Starts an agent on a task (or resumes one of its sessions) in a new agent host.</summary>
    private async Task<JsonNode?> StartAgentAsync(JsonObject project, UiWriteRequest request, CancellationToken token)
    {
        var body = new UiWriteBody(request.Body, "client", "prompt", "model", "resume", "mode");
        string taskId;
        using (var database = Open(project, tasks: true))
        {
            taskId = new TaskStore(database).Get(request.Route.Value("tid"), token).Id;
        }
        var key = await Agents.StartAsync(
            project,
            taskId,
            body.Text("client"),
            body.Text("prompt"),
            body.Text("model"),
            body.Text("resume"),
            body.Text("mode")
        );
        return UiJson.Node(new JsonObjectMap { ["key"] = key });
    }

    /// <summary>A command to a running agent session: send, stop, approve or close.</summary>
    private async Task<JsonNode?> AgentCommandAsync(JsonObject project, UiWriteRequest request, CancellationToken token)
    {
        var action = request.Route.Value("action");
        JsonObject command = new() { ["op"] = action };
        switch (action)
        {
            case "send":
                var text = new UiWriteBody(request.Body, "text").Text("text");
                if (text.Trim().Length is 0)
                {
                    throw new UiRequestException(400, "say something to the agent");
                }
                command["text"] = text;
                break;
            case "approve":
                var answer = new UiWriteBody(request.Body, "id", "allow");
                command["id"] = answer.Text("id");
                command["allow"] = answer.Flag("allow");
                break;
            case "stop" or "close":
                _ = new UiWriteBody(request.Body);
                break;
            default:
                throw new UiRequestException(404, "unknown agent action " + action);
        }
        await Agents.CommandAsync(project, request.Route.Value("key"), command, token);
        return UiJson.Node(new JsonObjectMap { ["ok"] = true });
    }

    private JsonObject AgentLog(JsonObject project, UiReadRequest request)
    {
        _ = long.TryParse(request.Parameter("after"), out var after);
        return Agents.Log(project, request.Value("key"), Math.Max(0, after));
    }

    /// <summary>
    /// The end of a session's transcript, read-only, for sessions harness did not start: prompts, answers and tool
    /// calls, the last first-come order kept, and the command to resume it in a terminal.
    /// </summary>
    private static JsonNode? TranscriptTail(TaskStore tasks, UiReadRequest request, CancellationToken token)
    {
        var (client, sessionId) = (request.Value("client"), request.Value("sid"));
        var session =
            tasks
                .Sessions(request.Value("tid"), token)
                .FirstOrDefault(s => s.Client == client && s.SessionId == sessionId)
            ?? throw new UiRequestException(404, "no such session on this task");
        JsonArray entries = [];
        var path = session.TranscriptPath;
        if (path.Length > 0 && File.Exists(path))
        {
            foreach (var entry in Tail(path, client).TakeLast(TailEntries))
            {
                entries.Add((JsonNode)entry);
            }
        }
        return UiJson.Node(
            new JsonObjectMap
            {
                ["client"] = client,
                ["session_id"] = sessionId,
                ["transcript_path"] = path,
                ["entries"] = entries,
                ["resume"] = ResumeCommand(client, sessionId),
            }
        );
    }

    private static List<JsonObject> Tail(string path, string client)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        var start = Math.Max(0, stream.Length - TailBytes);
        stream.Position = start;
        using var reader = new StreamReader(stream, new UTF8Encoding(false));
        if (start > 0)
        {
            reader.ReadLine(); // the first line read is cut
        }
        List<JsonObject> entries = [];
        while (reader.ReadLine() is { } line)
        {
            JsonObject? item = null;
            try
            {
                item = JsonNode.Parse(line) as JsonObject;
            }
            catch (JsonException)
            {
                // A line still being written.
            }
            if (item is not null)
            {
                entries.AddRange(client is AgentClients.Codex ? CodexEntries(item) : ClaudeEntries(item));
            }
        }
        return entries;
    }

    private static IEnumerable<JsonObject> ClaudeEntries(JsonObject line)
    {
        var at = Field(line["timestamp"]);
        var content = line["message"]?["content"];
        switch (Field(line["type"]))
        {
            case "user" when line["isMeta"] is null && content is JsonValue:
                yield return Entry(at, "user", Field(content), "");
                break;
            case "assistant" when content is JsonArray blocks:
                foreach (var block in blocks.OfType<JsonObject>())
                {
                    switch (Field(block["type"]))
                    {
                        case "text" when Field(block["text"]) is { Length: > 0 } text:
                            yield return Entry(at, "text", text, "");
                            break;
                        case "tool_use":
                            yield return Entry(at, "tool", Summary(block["input"]), Field(block["name"]));
                            break;
                        default:
                            break;
                    }
                }
                break;
            default:
                break;
        }
    }

    private static IEnumerable<JsonObject> CodexEntries(JsonObject line)
    {
        var at = Field(line["timestamp"]);
        var payload = line["payload"] as JsonObject;
        if (Field(line["type"]) is not "response_item" || payload is null)
        {
            yield break;
        }
        switch (Field(payload["type"]))
        {
            case "message":
                var role = Field(payload["role"]);
                var text = string.Join(
                    "\n",
                    (payload["content"] as JsonArray ?? [])
                        .Select(part => Field(part?["text"]))
                        .Where(part => part.Length > 0)
                );
                // what Codex injects as user messages (instructions, environment) starts with a tag
                if (
                    role is "assistant" or "user"
                    && text.Length > 0
                    && !text.TrimStart().StartsWith('<', StringComparison.Ordinal)
                )
                {
                    yield return Entry(at, role is "user" ? "user" : "text", text, "");
                }
                break;
            case "function_call" or "custom_tool_call":
                yield return Entry(
                    at,
                    "tool",
                    Summary(Field(payload["arguments"]) is { Length: > 0 } arguments ? arguments : payload["input"]),
                    Field(payload["name"])
                );
                break;
            default:
                break;
        }
    }

    private static JsonObject Entry(string at, string kind, string text, string tool)
    {
        var entry = new JsonObject
        {
            ["at"] = at,
            ["kind"] = kind,
            ["text"] = text.Length <= 2000 ? text : text[..1999] + "…",
        };
        if (tool.Length > 0)
        {
            entry["tool"] = tool;
        }
        return entry;
    }

    /// <summary>A tool call's input in a line: its command, path or pattern when it has one.</summary>
    private static string Summary(object? input)
    {
        var node = input switch
        {
            JsonNode json => json,
            string text when text.TrimStart().StartsWith('{', StringComparison.Ordinal) => Parse(text),
            string text => JsonValue.Create(text),
            _ => null,
        };
        if (node is JsonObject fields)
        {
            foreach (var key in (string[])["command", "cmd", "file_path", "path", "pattern", "query", "url"])
            {
                var value = fields[key];
                if (value is JsonArray parts)
                {
                    return string.Join(' ', parts.Select(Field));
                }
                if (Field(value) is { Length: > 0 } text)
                {
                    return text;
                }
            }
        }
        var line = (node is JsonValue ? Field(node) : node?.ToJsonString() ?? "").ReplaceLineEndings(" ");
        return line.Length <= 300 ? line : line[..299] + "…";
    }

    private static JsonNode? Parse(string text)
    {
        try
        {
            return JsonNode.Parse(text);
        }
        catch (JsonException)
        {
            return JsonValue.Create(text);
        }
    }

    private static string Field(JsonNode? node) =>
        node is JsonValue value && value.TryGetValue<string>(out var text) ? text : "";
}
