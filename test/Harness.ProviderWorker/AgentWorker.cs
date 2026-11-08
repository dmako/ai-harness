using System.Diagnostics;
using System.Globalization;
using System.Text.Json.Nodes;

namespace Harness.ProviderWorker;

/// <summary>
/// A scripted Claude Code (stream-json) or Codex (exec --json) CLI for the agent runner's tests. It reads the prompt
/// from standard input, logs its call to calls.jsonl in HARNESS_FAKE_AGENT_DIR and answers by HARNESS_FAKE_AGENT_MODE:
/// normal (one turn, then exit), linger (one turn, then never exit), hang (starts a turn and a descendant, never ends)
/// or fail (exits with an error before the turn ends).
/// </summary>
internal static class AgentWorker
{
    internal static async Task<int> RunAsync(string client, string[] args)
    {
        if (args.Contains("--input-format") || args.Contains("app-server"))
        {
            return await InteractiveAgentWorker.RunAsync(client, args);
        }
        var directory = Environment.GetEnvironmentVariable("HARNESS_FAKE_AGENT_DIR") ?? Path.GetTempPath();
        var mode = Environment.GetEnvironmentVariable("HARNESS_FAKE_AGENT_MODE") ?? "normal";
        var prompt = (await Console.In.ReadToEndAsync()).Trim();
        var resumed = client is "claude" ? After(args, "--resume") : CodexResumed(args);
        var session = resumed;
        if (session.Length is 0)
        {
            session = client is "claude" ? After(args, "--session-id") : Guid.NewGuid().ToString();
        }
        var call = new JsonObject
        {
            ["client"] = client,
            ["args"] = new JsonArray([.. args.Select(a => (JsonNode?)a)]),
            ["task"] = Environment.GetEnvironmentVariable("HARNESS_TASK_ID") ?? "",
            ["prompt"] = prompt,
            ["cwd"] = Environment.CurrentDirectory,
            ["pid"] = Environment.ProcessId,
            ["session"] = session,
        };
        await File.AppendAllTextAsync(Path.Combine(directory, "calls.jsonl"), call.ToJsonString() + "\n");
        if (mode is "fail")
        {
            await Console.Error.WriteLineAsync("fake agent: boom");
            return 3;
        }
        if (mode is "hang" or "linger")
        {
            // Started before any output, so the test knows both processes before the run can stop them.
            await SpawnAsync(Path.Combine(directory, "descendant.pid"));
            await File.WriteAllTextAsync(
                Path.Combine(directory, "agent.pid"),
                Environment.ProcessId.ToString(CultureInfo.InvariantCulture)
            );
        }
        var answer = resumed.Length > 0 ? "resumed " + resumed + ": " + prompt : "started: " + prompt;
        var lines = client is "claude"
            ? Claude(session, answer, mode is "hang")
            : Codex(session, answer, mode is "hang");
        foreach (var line in lines)
        {
            await Console.Out.WriteLineAsync(line.ToJsonString());
            await Console.Out.FlushAsync();
        }
        if (mode is "hang" or "linger")
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, CancellationToken.None);
        }
        return 0;
    }

    private static string After(string[] args, string name)
    {
        var index = Array.IndexOf(args, name);
        return index >= 0 && index + 1 < args.Length ? args[index + 1] : "";
    }

    /// <summary>codex exec resume [options] &lt;thread&gt; -: the thread is the argument before the prompt's "-".</summary>
    private static string CodexResumed(string[] args) =>
        args.Contains("resume") && args.Length >= 2 && args[^1] is "-" ? args[^2] : "";

    private static IEnumerable<JsonObject> Claude(string session, string answer, bool hang)
    {
        yield return new()
        {
            ["type"] = "system",
            ["subtype"] = "init",
            ["session_id"] = session,
            ["model"] = "claude-fake-1",
        };
        yield return new()
        {
            ["type"] = "assistant",
            ["session_id"] = session,
            ["message"] = new JsonObject
            {
                ["model"] = "claude-fake-1",
                ["content"] = new JsonArray(
                    new JsonObject
                    {
                        ["type"] = "tool_use",
                        ["name"] = "Bash",
                        ["input"] = new JsonObject { ["command"] = "dotnet build" },
                    }
                ),
            },
        };
        if (hang)
        {
            yield break;
        }
        yield return new()
        {
            ["type"] = "assistant",
            ["session_id"] = session,
            ["message"] = new JsonObject
            {
                ["model"] = "claude-fake-1",
                ["content"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = answer }),
            },
        };
        yield return new()
        {
            ["type"] = "result",
            ["subtype"] = "success",
            ["is_error"] = false,
            ["session_id"] = session,
            ["result"] = answer,
            ["duration_ms"] = 1234,
            ["total_cost_usd"] = 0.0125,
            ["usage"] = new JsonObject
            {
                ["input_tokens"] = 10,
                ["cache_read_input_tokens"] = 200,
                ["cache_creation_input_tokens"] = 30,
                ["output_tokens"] = 40,
            },
        };
    }

    private static IEnumerable<JsonObject> Codex(string thread, string answer, bool hang)
    {
        yield return new() { ["type"] = "thread.started", ["thread_id"] = thread };
        yield return new() { ["type"] = "turn.started" };
        yield return new()
        {
            ["type"] = "item.completed",
            ["item"] = new JsonObject
            {
                ["id"] = "item_0",
                ["type"] = "command_execution",
                ["command"] = "dotnet test",
                ["status"] = "completed",
            },
        };
        if (hang)
        {
            yield break;
        }
        yield return new()
        {
            ["type"] = "item.completed",
            ["item"] = new JsonObject
            {
                ["id"] = "item_1",
                ["type"] = "agent_message",
                ["text"] = answer,
            },
        };
        yield return new()
        {
            ["type"] = "turn.completed",
            ["usage"] = new JsonObject
            {
                ["input_tokens"] = 1000,
                ["cached_input_tokens"] = 800,
                ["output_tokens"] = 50,
                ["reasoning_output_tokens"] = 20,
            },
        };
    }

    private static async Task SpawnAsync(string pidFile)
    {
        var start = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false, CreateNoWindow = true };
        if (
            string.Equals(
                Path.GetFileNameWithoutExtension(Environment.ProcessPath),
                "dotnet",
                StringComparison.OrdinalIgnoreCase
            )
        )
        {
            start.ArgumentList.Add(typeof(AgentWorker).Assembly.Location);
        }
        start.ArgumentList.Add("descendant");
        start.ArgumentList.Add(pidFile);
        start.Environment.Remove("HARNESS_FAKE_AGENT");
        using var child = Process.Start(start)!;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (!File.Exists(pidFile))
        {
            await Task.Delay(10, timeout.Token);
        }
    }
}
