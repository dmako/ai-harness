using System.Globalization;
using System.Text.Json.Nodes;

namespace Harness.ProviderWorker;

/// <summary>
/// Scripted interactive clients for the agent host's tests: Claude Code with stream-json input
/// (--input-format stream-json, --permission-prompt-tool stdio) and `codex app-server` (JSON-RPC). A turn's text
/// decides what happens: "approve" asks for a tool permission and answers by the decision, "slow" works until it is
/// interrupted, "exit" ends the process, anything else is echoed. Each start is logged to calls.jsonl.
/// </summary>
internal static class InteractiveAgentWorker
{
    private static readonly SemaphoreSlim Writes = new(1, 1);

    internal static async Task<int> RunAsync(string client, string[] args)
    {
        var directory = Environment.GetEnvironmentVariable("HARNESS_FAKE_AGENT_DIR") ?? Path.GetTempPath();
        await File.AppendAllTextAsync(
            Path.Combine(directory, "calls.jsonl"),
            new JsonObject
            {
                ["client"] = client,
                ["args"] = new JsonArray([.. args.Select(a => (JsonNode?)a)]),
                ["task"] = Environment.GetEnvironmentVariable("HARNESS_TASK_ID") ?? "",
                ["cwd"] = Environment.CurrentDirectory,
                ["pid"] = Environment.ProcessId,
            }.ToJsonString() + "\n"
        );
        if (Environment.GetEnvironmentVariable("HARNESS_FAKE_AGENT_MODE") is "fail")
        {
            await Console.Error.WriteLineAsync("fake agent: boom");
            return 3;
        }
        return client is "claude" ? await ClaudeAsync(args) : await CodexAsync();
    }

    private static async Task Write(JsonObject line)
    {
        await Writes.WaitAsync();
        try
        {
            await Console.Out.WriteLineAsync(line.ToJsonString());
            await Console.Out.FlushAsync();
        }
        finally
        {
            Writes.Release();
        }
    }

    private static async Task<JsonObject?> ReadAsync()
    {
        while (await Console.In.ReadLineAsync() is { } line)
        {
            if (line.Trim().Length > 0 && JsonNode.Parse(line) is JsonObject json)
            {
                return json;
            }
        }
        return null;
    }

    private static string After(string[] args, string name)
    {
        var index = Array.IndexOf(args, name);
        return index >= 0 && index + 1 < args.Length ? args[index + 1] : "";
    }

    private static async Task<int> ClaudeAsync(string[] args)
    {
        var resumed = After(args, "--resume");
        var session = resumed.Length > 0 ? resumed : After(args, "--session-id");
        var requests = 0;
        while (await ReadAsync() is { } message)
        {
            if ((string?)message["type"] is not "user")
            {
                continue;
            }
            var text = (string?)message["message"]?["content"] ?? "";
            await Write(
                new()
                {
                    ["type"] = "system",
                    ["subtype"] = "init",
                    ["session_id"] = session,
                    ["model"] = "claude-fake-1",
                }
            );
            var answer = (resumed.Length > 0 ? "resumed " + resumed + ": " : "echo: ") + text;
            var subtype = "success";
            if (text.Contains("exit", StringComparison.Ordinal))
            {
                return 4;
            }
            if (text.Contains("approve", StringComparison.Ordinal))
            {
                var id = "req-" + (++requests).ToString(CultureInfo.InvariantCulture);
                await Write(Assistant(session, new JsonObject { ["type"] = "tool_use", ["name"] = "Write" }));
                await Write(
                    new()
                    {
                        ["type"] = "control_request",
                        ["request_id"] = id,
                        ["request"] = new JsonObject
                        {
                            ["subtype"] = "can_use_tool",
                            ["tool_name"] = "Write",
                            ["input"] = new JsonObject { ["file_path"] = "notes.txt", ["content"] = "x" },
                        },
                    }
                );
                var response = await ReadAsync();
                var behavior = (string?)response?["response"]?["response"]?["behavior"];
                var updated = response?["response"]?["response"]?["updatedInput"]?["file_path"];
                answer = behavior is "allow" ? "allowed " + (string?)updated : "denied";
            }
            else if (text.Contains("slow", StringComparison.Ordinal))
            {
                await Write(Assistant(session, new JsonObject { ["type"] = "text", ["text"] = "working" }));
                while (await ReadAsync() is { } control)
                {
                    if ((string?)control["request"]?["subtype"] is "interrupt")
                    {
                        await Write(
                            new()
                            {
                                ["type"] = "control_response",
                                ["response"] = new JsonObject
                                {
                                    ["subtype"] = "success",
                                    ["request_id"] = control["request_id"]?.DeepClone(),
                                },
                            }
                        );
                        break;
                    }
                }
                subtype = "error_during_execution";
            }
            if (subtype is "success")
            {
                await Write(Assistant(session, new JsonObject { ["type"] = "text", ["text"] = answer }));
            }
            await Write(
                new()
                {
                    ["type"] = "result",
                    ["subtype"] = subtype,
                    ["is_error"] = subtype is not "success",
                    ["session_id"] = session,
                    ["result"] = subtype is "success" ? answer : null,
                    ["duration_ms"] = 321,
                    ["total_cost_usd"] = 0.01,
                    ["usage"] = new JsonObject
                    {
                        ["input_tokens"] = 1,
                        ["cache_read_input_tokens"] = 2,
                        ["cache_creation_input_tokens"] = 3,
                        ["output_tokens"] = 4,
                    },
                }
            );
        }
        return 0;
    }

    private static JsonObject Assistant(string session, JsonObject block) =>
        new()
        {
            ["type"] = "assistant",
            ["session_id"] = session,
            ["message"] = new JsonObject { ["model"] = "claude-fake-1", ["content"] = new JsonArray(block) },
        };

    private static async Task<int> CodexAsync()
    {
        var thread = "";
        var turns = 0;
        long output = 0;
        while (await ReadAsync() is { } message)
        {
            var id = message["id"]?.DeepClone();
            switch ((string?)message["method"])
            {
                case "initialize":
                    await Respond(id, new JsonObject { ["userAgent"] = "fake" });
                    break;
                case "thread/start":
                    thread = Guid.NewGuid().ToString();
                    await Respond(
                        id,
                        new JsonObject
                        {
                            ["thread"] = new JsonObject { ["id"] = thread },
                            ["model"] = "gpt-fake",
                        }
                    );
                    break;
                case "thread/resume":
                    thread = (string?)message["params"]?["threadId"] ?? "";
                    await Respond(
                        id,
                        new JsonObject
                        {
                            ["thread"] = new JsonObject { ["id"] = thread },
                            ["model"] = "gpt-fake",
                        }
                    );
                    break;
                case "turn/start":
                    var turn = "turn-" + (++turns).ToString(CultureInfo.InvariantCulture);
                    var text = (string?)message["params"]?["input"]?[0]?["text"] ?? "";
                    await Respond(
                        id,
                        new JsonObject
                        {
                            ["turn"] = new JsonObject { ["id"] = turn, ["status"] = "inProgress" },
                        }
                    );
                    await Notify("turn/started", new JsonObject { ["turn"] = new JsonObject { ["id"] = turn } });
                    if (text.Contains("exit", StringComparison.Ordinal))
                    {
                        return 4;
                    }
                    var status = await CodexTurnAsync(turn, text);
                    output += 5;
                    await Notify(
                        "thread/tokenUsage/updated",
                        new JsonObject
                        {
                            ["threadId"] = thread,
                            ["turnId"] = turn,
                            ["tokenUsage"] = new JsonObject
                            {
                                ["total"] = new JsonObject
                                {
                                    ["inputTokens"] = 100 * turns,
                                    ["cachedInputTokens"] = 60 * turns,
                                    ["outputTokens"] = output,
                                    ["reasoningOutputTokens"] = 0,
                                    ["totalTokens"] = 100 * turns + output,
                                },
                            },
                        }
                    );
                    await Notify(
                        "turn/completed",
                        new JsonObject
                        {
                            ["threadId"] = thread,
                            ["turn"] = new JsonObject
                            {
                                ["id"] = turn,
                                ["status"] = status,
                                ["durationMs"] = 222,
                            },
                        }
                    );
                    break;
                default:
                    break;
            }
        }
        return 0;
    }

    /// <summary>One Codex turn's items; returns how it ended.</summary>
    private static async Task<string> CodexTurnAsync(string turn, string text)
    {
        if (text.Contains("approve", StringComparison.Ordinal))
        {
            await Write(
                new()
                {
                    ["jsonrpc"] = "2.0",
                    ["id"] = 0,
                    ["method"] = "item/commandExecution/requestApproval",
                    ["params"] = new JsonObject
                    {
                        ["turnId"] = turn,
                        ["itemId"] = "exec-1",
                        ["command"] = "mkdir probe",
                    },
                }
            );
            var answer = await ReadAsync();
            var decision = (string?)answer?["result"]?["decision"];
            await Item("agentMessage", decision is "accept" ? "accepted" : "declined");
            return "completed";
        }
        if (text.Contains("slow", StringComparison.Ordinal))
        {
            await Item("agentMessage", "working");
            while (await ReadAsync() is { } message)
            {
                if ((string?)message["method"] is "turn/interrupt")
                {
                    await Respond(message["id"]?.DeepClone(), []);
                    return "interrupted";
                }
            }
        }
        await Item("commandExecution", "dotnet test");
        await Item("agentMessage", "echo: " + text);
        return "completed";
    }

    private static Task Item(string type, string text) =>
        Notify(
            "item/completed",
            new JsonObject
            {
                ["item"] = type is "commandExecution"
                    ? new JsonObject { ["type"] = type, ["command"] = text }
                    : new JsonObject { ["type"] = type, ["text"] = text },
            }
        );

    private static Task Respond(JsonNode? id, JsonObject result) =>
        Write(
            new()
            {
                ["jsonrpc"] = "2.0",
                ["id"] = id,
                ["result"] = result,
            }
        );

    private static Task Notify(string method, JsonObject parameters) =>
        Write(
            new()
            {
                ["jsonrpc"] = "2.0",
                ["method"] = method,
                ["params"] = parameters,
            }
        );
}
