using System.Text.Json.Nodes;
using Harness.Core.Agents;
using Harness.Core.Providers;
using Harness.Core.Tasks;
using Harness.Infrastructure.Processes;
using Harness.Infrastructure.Storage;
using Harness.Infrastructure.Tasks;

namespace Harness.Agents;

/// <summary>
/// harness-agents: runs one agent turn for a task and owns its process, so the session outlives whoever started it
/// (harness ui). It writes the turn's events to standard output as JSON lines, ending with an "outcome" line, and
/// records the session in the project database (agent_sessions, origin harness). A "cancel" line on standard input,
/// Ctrl+C or SIGTERM stops the turn and every process the client started.
/// </summary>
public static class AgentHost
{
    public const int Completed = 0;
    public const int Failed = 1;
    public const int Usage = 2;
    public const int Cancelled = 3;

    public const string Help = """
        Usage: harness-agents run|serve --client claude-code|codex --task <id> --cwd <dir>
                                  (--prompt <text> | --prompt-file <file>) [--resume <session>] [--model <model>]
                                  [--db <project database>] [--arg <client argument>]... [--env <name=value>]...
                                  [--command <executable> [--command-arg <argument>]...]
                                  serve: --key <32 hex digits> --state <dir> [--idle-minutes <n>], prompt optional
        run: one agent turn for a task; events go to standard output as JSON lines, ending with "outcome";
        a "cancel" line on standard input stops it. Exit code 0 completed, 1 failed, 2 usage, 3 cancelled.
        serve: keeps the session open and takes commands (send, stop, approve, close) on its named pipe.
        """;

    public static async Task<int> RunAsync(
        IReadOnlyList<string> args,
        TextReader input,
        TextWriter output,
        TextWriter error,
        CancellationToken lifetime,
        IProviderProcessFactory? processes = null
    )
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(error);
        if (!HostOptions.TryParse(args, out var options, out var problem))
        {
            await error.WriteLineAsync(problem);
            await error.WriteLineAsync(Help);
            return Usage;
        }
        if (options.Mode is "serve")
        {
            return await AgentServer.ServeAsync(options, error, processes ?? new ProviderProcessFactory(), lifetime);
        }
        var runner = Runner(options, processes ?? new ProviderProcessFactory());
        using var database = options.Database.Length > 0 ? HarnessDatabase.OpenExisting(options.Database) : null;
        var recorder = new SessionRecorder(database, runner.Client, options.Start.TaskId, error);
        IAgentRun run;
        try
        {
            run =
                options.Resume.Length > 0
                    ? await runner.ResumeAsync(options.Resume, options.Start, lifetime)
                    : await runner.StartAsync(options.Start, lifetime);
        }
        catch (Exception exception) when (exception is IOException or System.ComponentModel.Win32Exception)
        {
            await Write(output, Outcome(new(AgentRunStatus.Failed, options.Resume) { Error = exception.Message }));
            return Failed;
        }
        await using (run)
        {
            recorder.Working(run.SessionId);
            using var registration = lifetime.Register(() => _ = run.CancelAsync());
            using var stop = new CancellationTokenSource();
            _ = ListenAsync(input, run, stop.Token);
            await foreach (var item in run.Events)
            {
                if (item.Kind is AgentEventKind.Session)
                {
                    recorder.Working(item.SessionId);
                }
                await Write(output, Event(item));
            }
            var outcome = await run.Completion;
            await stop.CancelAsync();
            recorder.Ended(outcome);
            await Write(output, Outcome(outcome));
            return outcome.Status switch
            {
                AgentRunStatus.Completed => Completed,
                AgentRunStatus.Cancelled => Cancelled,
                _ => Failed,
            };
        }
    }

    private static IAgentRunner Runner(HostOptions options, IProviderProcessFactory processes) =>
        options.Client switch
        {
            AgentClients.Codex => new CodexRunner(processes, options.Command),
            _ => new ClaudeRunner(processes, options.Command),
        };

    /// <summary>Cancels the run on a "cancel" line; standard input closing (a detached host) changes nothing.</summary>
    private static async Task ListenAsync(TextReader input, IAgentRun run, CancellationToken cancellationToken)
    {
        try
        {
            while (await input.ReadLineAsync(cancellationToken) is { } line)
            {
                if (line.Trim() is "cancel")
                {
                    await run.CancelAsync();
                    return;
                }
            }
        }
        catch (Exception exception) when (exception is OperationCanceledException or IOException)
        {
            // The run ended, or there is no input to listen to.
        }
    }

    private static async Task Write(TextWriter output, JsonObject line)
    {
        await output.WriteLineAsync(line.ToJsonString());
        await output.FlushAsync();
    }

    internal static JsonObject Event(AgentEvent item)
    {
        var line = new JsonObject
        {
            ["event"] = item.Kind switch
            {
                AgentEventKind.Session => "session",
                AgentEventKind.Text => "text",
                AgentEventKind.ToolCall => "tool",
                AgentEventKind.Usage => "usage",
                _ => "finished",
            },
            ["at"] = item.At.UtcDateTime.ToString(
                "yyyy-MM-dd'T'HH:mm:ss.fff'Z'",
                System.Globalization.CultureInfo.InvariantCulture
            ),
            ["session_id"] = item.SessionId,
        };
        Optional(line, "text", item.Text);
        Optional(line, "tool", item.Tool);
        Optional(line, "model", item.Model);
        if (item.Usage is { } usage)
        {
            line["usage"] = Tokens(usage);
        }
        return line;
    }

    internal static JsonObject Outcome(AgentOutcome outcome)
    {
        var line = new JsonObject
        {
            ["event"] = "outcome",
            ["status"] = outcome.Status switch
            {
                AgentRunStatus.Completed => "completed",
                AgentRunStatus.Cancelled => "cancelled",
                _ => "failed",
            },
            ["session_id"] = outcome.SessionId,
        };
        Optional(line, "text", outcome.Text);
        Optional(line, "error", outcome.Error);
        Optional(line, "model", outcome.Model);
        line["usage"] = Tokens(outcome.Usage);
        if (outcome.CostUsd is { } cost)
        {
            line["cost_usd"] = cost;
        }
        line["duration_ms"] = outcome.DurationMs;
        return line;
    }

    private static JsonObject Tokens(TokenCounts tokens) =>
        new()
        {
            ["input_tokens"] = tokens.Input,
            ["cache_read_tokens"] = tokens.CachedInput,
            ["cache_write_tokens"] = tokens.CacheWrite,
            ["output_tokens"] = tokens.Output,
            ["reasoning_tokens"] = tokens.Reasoning,
        };

    private static void Optional(JsonObject line, string name, string value)
    {
        if (value.Length > 0)
        {
            line[name] = value;
        }
    }

    /// <summary>Writes the session's row as the run goes; a database problem is reported, never fatal to the run.</summary>
    private sealed class SessionRecorder(HarnessDatabase? database, string client, string taskId, TextWriter error)
    {
        private string _recorded = "";

        public void Working(string sessionId)
        {
            if (sessionId.Length > 0 && sessionId != _recorded && Record(sessionId, "working"))
            {
                _recorded = sessionId;
            }
        }

        public void Ended(AgentOutcome outcome)
        {
            var status = outcome.Status switch
            {
                AgentRunStatus.Completed => "idle",
                AgentRunStatus.Cancelled => "cancelled",
                _ => "failed",
            };
            Record(outcome.SessionId.Length > 0 ? outcome.SessionId : _recorded, status);
        }

        private bool Record(string sessionId, string status)
        {
            if (database is null || sessionId.Length is 0)
            {
                return false;
            }
            try
            {
                if (new TaskStore(database).RecordRun(client, sessionId, taskId, status, DateTimeOffset.UtcNow))
                {
                    return true;
                }
                error.WriteLine("harness-agents: task " + taskId + " is not in the project database");
            }
            catch (Exception exception)
                when (exception is IOException or Microsoft.Data.Sqlite.SqliteException or InvalidOperationException)
            {
                error.WriteLine("harness-agents: could not record the session: " + exception.Message);
            }
            return false;
        }
    }
}

/// <summary>The host's command line.</summary>
internal sealed record HostOptions(
    string Client,
    AgentStart Start,
    string Resume,
    string Database,
    AgentCommand? Command
)
{
    /// <summary>run (one turn) or serve (a session kept open, steered through its pipe).</summary>
    public string Mode { get; init; } = "run";

    /// <summary>serve: the host's key, which names its pipe and its files in State.</summary>
    public string Key { get; init; } = "";

    /// <summary>serve: the folder for the host's state and event log.</summary>
    public string State { get; init; } = "";

    /// <summary>serve: how long a session may sit idle before the host closes it.</summary>
    public TimeSpan Idle { get; init; } = TimeSpan.FromMinutes(60);

    public static bool TryParse(IReadOnlyList<string> args, out HostOptions options, out string problem)
    {
        options = new("", new("", "", ""), "", "", null);
        if (args.Count is 0 || args[0] is not ("run" or "serve"))
        {
            problem = "harness-agents: expected the command 'run' or 'serve'";
            return false;
        }
        Dictionary<string, string> single = new(StringComparer.Ordinal);
        List<string> clientArguments = [];
        List<string> commandArguments = [];
        Dictionary<string, string> environment = new(StringComparer.Ordinal);
        if (args.Count % 2 is 0)
        {
            problem = "harness-agents: every option needs a value";
            return false;
        }
        for (var pair = 1; pair < args.Count; pair += 2)
        {
            var (name, value) = (args[pair], args[pair + 1]);
            if (!name.StartsWith("--", StringComparison.Ordinal))
            {
                problem = "harness-agents: unexpected argument " + name;
                return false;
            }
            switch (name)
            {
                case "--arg":
                    clientArguments.Add(value);
                    break;
                case "--command-arg":
                    commandArguments.Add(value);
                    break;
                case "--env" when value.Split('=', 2) is [{ Length: > 0 } variable, var setting]:
                    environment[variable] = setting;
                    break;
                case "--client"
                or "--task"
                or "--cwd"
                or "--prompt"
                or "--prompt-file"
                or "--resume"
                or "--model"
                or "--db"
                or "--command"
                or "--key"
                or "--state"
                or "--idle-minutes":
                    single[name] = value;
                    break;
                default:
                    problem = "harness-agents: unknown option " + name;
                    return false;
            }
        }
        var client = single.GetValueOrDefault("--client", "");
        if (client is not (AgentClients.Claude or AgentClients.Codex))
        {
            problem = "harness-agents: --client must be claude-code or codex";
            return false;
        }
        var task = single.GetValueOrDefault("--task", "");
        var directory = single.GetValueOrDefault("--cwd", "");
        var prompt = single.TryGetValue("--prompt-file", out var file)
            ? File.ReadAllText(file)
            : single.GetValueOrDefault("--prompt", "");
        var serve = args[0] is "serve";
        if (task.Length is 0 || directory.Length is 0 || (!serve && prompt.Trim().Length is 0))
        {
            problem = "harness-agents: --task, --cwd and a prompt are required";
            return false;
        }
        var key = single.GetValueOrDefault("--key", "");
        var state = single.GetValueOrDefault("--state", "");
        if (serve && (!Harness.Infrastructure.Agents.AgentHostFiles.IsKey(key) || state.Length is 0))
        {
            problem = "harness-agents: serve needs --key (32 hex digits) and --state";
            return false;
        }
        var idle = 60;
        if (
            single.TryGetValue("--idle-minutes", out var minutes)
            && (!int.TryParse(minutes, System.Globalization.CultureInfo.InvariantCulture, out idle) || idle < 1)
        )
        {
            problem = "harness-agents: --idle-minutes must be a positive number";
            return false;
        }
        var command = single.TryGetValue("--command", out var executable)
            ? new AgentCommand(executable, commandArguments)
            : null;
        options = new(
            client,
            new(task, Path.GetFullPath(directory), prompt)
            {
                Model = single.GetValueOrDefault("--model", ""),
                Arguments = clientArguments,
                Environment = environment,
            },
            single.GetValueOrDefault("--resume", ""),
            single.GetValueOrDefault("--db", ""),
            command
        )
        {
            Mode = args[0],
            Key = key,
            State = state.Length > 0 ? Path.GetFullPath(state) : "",
            Idle = TimeSpan.FromMinutes(idle),
        };
        problem = "";
        return true;
    }
}
