using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json.Nodes;
using Harness.Core.Agents;
using Harness.Core.Providers;
using Harness.Core.Tasks;

namespace Harness.Agents;

/// <summary>
/// Codex through `codex app-server` (JSON-RPC over standard input and output): initialize, then thread/start or
/// thread/resume; each turn is turn/start, stopped with turn/interrupt. Command and file-change approvals come as
/// requests from the server and are answered accept or decline. The thread's approval policy and sandbox come from the
/// session's permission mode (<see cref="AgentPermissionModes"/>): Manual (untrusted) asks before any command Codex
/// does not know to be safe, Auto (on-request) may run one without asking and relies on the workspace-write sandbox.
/// </summary>
internal sealed class CodexSession : InteractiveSession
{
    private static readonly TimeSpan Answer = TimeSpan.FromSeconds(60);

    private readonly ConcurrentDictionary<long, TaskCompletionSource<JsonObject>> _calls = new();
    private readonly ConcurrentDictionary<string, JsonNode> _pending = new(StringComparer.Ordinal);
    private readonly AgentStart _start;
    private readonly AgentPermissionMode _mode;
    private readonly string _resume;
    private long _next;
    private string _turn = "";
    private TokenCounts _total = TokenCounts.Zero;
    private TokenCounts _before = TokenCounts.Zero;
    private string _lastError = "";

    private CodexSession(
        IProviderProcess process,
        Action<JsonObject> emit,
        AgentStart start,
        AgentPermissionMode mode,
        string resume
    )
        : base(process, emit)
    {
        _start = start;
        _mode = mode;
        _resume = resume;
        Model = start.Model;
    }

    public override string Client => AgentClients.Codex;

    public static CodexSession Start(
        IProviderProcessFactory processes,
        AgentCommand command,
        AgentStart start,
        string resume,
        Action<JsonObject> emit
    )
    {
        // an unknown mode throws before any process starts
        var mode = AgentPermissionModes.Resolve(AgentClients.Codex, start.PermissionMode);
        var process = processes.Start(
            new ProviderProcessOptions(command.Executable, [.. command.Prefix, "app-server", .. start.Arguments])
            {
                WorkingDirectory = start.WorkingDirectory,
                Environment = AgentEnvironment.For(start),
            }
        );
        return new CodexSession(process, emit, start, mode, resume);
    }

    protected override async Task HandshakeAsync(CancellationToken cancellationToken)
    {
        var initialized = await CallAsync(
            "initialize",
            new JsonObject
            {
                ["clientInfo"] = new JsonObject { ["name"] = "harness-agents", ["version"] = Version() },
            },
            cancellationToken
        );
        await WriteAsync(new() { ["jsonrpc"] = "2.0", ["method"] = "initialized" }, cancellationToken);
        // beside the thread's opening: the session never waits for the model list
        _ = ListModelsAsync(AgentModelProtocol.CodexVersion(initialized), cancellationToken);
        var settings = new JsonObject
        {
            ["cwd"] = _start.WorkingDirectory,
            ["approvalPolicy"] = _mode.ApprovalPolicy,
            ["sandbox"] = _mode.Sandbox,
        };
        if (_start.Model.Length > 0)
        {
            settings["model"] = _start.Model;
        }
        if (_resume.Length > 0)
        {
            settings["threadId"] = _resume;
        }
        var opened = await CallAsync(
            _resume.Length > 0 ? "thread/resume" : "thread/start",
            settings,
            cancellationToken
        );
        SessionId = Text(opened["thread"]?["id"]) is { Length: > 0 } thread ? thread : _resume;
        Model = Text(opened["model"]) is { Length: > 0 } model ? model : Model;
        EmitEvent(
            new AgentEvent(AgentEventKind.Session, DateTimeOffset.UtcNow) { SessionId = SessionId, Model = Model }
        );
    }

    /// <summary>model/list, page by page, for the model catalog (TASK-0142).</summary>
    private async Task ListModelsAsync(string version, CancellationToken cancellationToken)
    {
        if (ModelsReported is null && ModelsFailed is null)
        {
            return;
        }
        try
        {
            List<AgentModel> models = [];
            var cursor = "";
            for (var page = 0; page < AgentModelProtocol.CodexPages; page++)
            {
                var (found, next) = AgentModelProtocol.Codex(
                    await CallAsync("model/list", AgentModelProtocol.CodexListParams(cursor), cancellationToken)
                );
                models.AddRange(found);
                if (next.Length is 0)
                {
                    break;
                }
                cursor = next;
            }
            ModelsReported?.Invoke(new(models) { Version = version });
        }
        catch (Exception exception)
            when (exception
                    is InvalidOperationException
                        or TimeoutException
                        or IOException
                        or OperationCanceledException
                        or ObjectDisposedException
            )
        {
            if (!cancellationToken.IsCancellationRequested)
            {
                ModelsFailed?.Invoke(exception.Message);
            }
        }
    }

    protected override async Task StartTurnAsync(string text, CancellationToken cancellationToken)
    {
        _before = _total;
        _lastError = "";
        var started = await CallAsync(
            "turn/start",
            new JsonObject
            {
                ["threadId"] = SessionId,
                ["input"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = text }),
            },
            cancellationToken
        );
        if (Text(started["turn"]?["id"]) is { Length: > 0 } turn)
        {
            _turn = turn;
        }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        if (!Busy || _turn.Length is 0)
        {
            return;
        }
        await CallAsync(
            "turn/interrupt",
            new JsonObject { ["threadId"] = SessionId, ["turnId"] = _turn },
            cancellationToken
        );
    }

    public override async Task AnswerAsync(string id, bool allow, CancellationToken cancellationToken)
    {
        if (!_pending.TryRemove(id, out var request))
        {
            throw new KeyNotFoundException("no approval request " + id + " is waiting");
        }
        // before the answer reaches the client, so its reaction is never logged ahead of the answer
        Answered(id, allow);
        await WriteAsync(
            new()
            {
                ["jsonrpc"] = "2.0",
                ["id"] = request.DeepClone(),
                ["result"] = new JsonObject { ["decision"] = allow ? "accept" : "decline" },
            },
            cancellationToken
        );
    }

    protected override void Read(JsonObject line)
    {
        var method = Text(line["method"]);
        if (method.Length is 0)
        {
            Response(line);
        }
        else if (line["id"] is { } id)
        {
            ServerRequest(method, id, line["params"] as JsonObject);
        }
        else
        {
            Notification(method, line["params"] as JsonObject);
        }
    }

    private void Response(JsonObject line)
    {
        if (line["id"] is not JsonValue value || !value.TryGetValue<long>(out var id))
        {
            return;
        }
        if (_calls.TryRemove(id, out var call))
        {
            if (line["error"] is JsonObject error)
            {
                call.TrySetException(new InvalidOperationException("codex: " + Text(error["message"])));
            }
            else
            {
                call.TrySetResult(line["result"] as JsonObject ?? []);
            }
        }
    }

    private void ServerRequest(string method, JsonNode id, JsonObject? parameters)
    {
        var (tool, text) = method switch
        {
            "item/commandExecution/requestApproval" => ("shell", Text(parameters?["command"])),
            "item/fileChange/requestApproval" => ("apply_patch", Text(parameters?["reason"])),
            _ => ("", ""),
        };
        if (tool.Length is 0)
        {
            // Nothing harness answers (user input, elicitations, auth): the server's own fallback applies.
            _ = WriteAsync(
                new()
                {
                    ["jsonrpc"] = "2.0",
                    ["id"] = id.DeepClone(),
                    ["error"] = new JsonObject { ["code"] = -32601, ["message"] = "not supported by harness" },
                },
                CancellationToken.None
            );
            return;
        }
        var key = "codex-" + id.ToJsonString().Trim('"');
        _pending[key] = id.DeepClone();
        Approval(key, tool, Short(text.Length > 0 ? text : Text(parameters?["itemId"])));
    }

    private void Notification(string method, JsonObject? parameters)
    {
        var at = DateTimeOffset.UtcNow;
        switch (method)
        {
            case "turn/started":
                _turn = Text(parameters?["turn"]?["id"]) is { Length: > 0 } turn ? turn : _turn;
                break;
            case "item/completed":
                Item(parameters?["item"] as JsonObject, at);
                break;
            case "thread/tokenUsage/updated":
                var usage = parameters?["tokenUsage"] as JsonObject;
                if (usage?["total"] is JsonObject total)
                {
                    _total = Tokens(total);
                }
                break;
            case "error":
                _lastError = Text(parameters?["error"]?["message"]);
                break;
            case "turn/completed":
                Completed(parameters?["turn"] as JsonObject);
                break;
            default:
                break;
        }
    }

    private void Item(JsonObject? item, DateTimeOffset at)
    {
        var (tool, text) = Text(item?["type"]) switch
        {
            "agentMessage" => ("", Text(item?["text"])),
            "commandExecution" => ("shell", Text(item?["command"])),
            "mcpToolCall" => (Text(item?["server"]) + "." + Text(item?["tool"]), ""),
            "fileChange" => ("apply_patch", Paths(item)),
            "webSearch" => ("web_search", Text(item?["query"])),
            _ => ("", ""),
        };
        if (tool.Length > 0)
        {
            EmitEvent(
                new AgentEvent(AgentEventKind.ToolCall, at)
                {
                    SessionId = SessionId,
                    Tool = tool,
                    Text = Short(text),
                }
            );
        }
        else if (text.Length > 0)
        {
            EmitEvent(new AgentEvent(AgentEventKind.Text, at) { SessionId = SessionId, Text = text });
        }
    }

    private static string Paths(JsonObject? item)
    {
        var changes = item?["changes"] as JsonArray ?? [];
        return string.Join(", ", changes.Select(change => Text(change?["path"])).Where(path => path.Length > 0));
    }

    private void Completed(JsonObject? turn)
    {
        _pending.Clear();
        var status = Text(turn?["status"]) switch
        {
            "completed" => "completed",
            "interrupted" => "stopped",
            _ => "failed",
        };
        var usage = new TokenCounts(
            Math.Max(0, _total.Input - _before.Input),
            Math.Max(0, _total.CachedInput - _before.CachedInput),
            Math.Max(0, _total.CacheWrite - _before.CacheWrite),
            Math.Max(0, _total.Output - _before.Output),
            Math.Max(0, _total.Reasoning - _before.Reasoning)
        );
        var error = Text(turn?["error"]?["message"]);
        if (error.Length is 0)
        {
            error = _lastError;
        }
        TurnEnded(status, usage, status is "failed" ? error : "", ms: Number(turn?["durationMs"]));
    }

    /// <summary>A usage breakdown: input includes what was read from or written to the cache.</summary>
    private static TokenCounts Tokens(JsonObject total)
    {
        var input = Number(total["inputTokens"]);
        var cached = Number(total["cachedInputTokens"]);
        var written = Number(total["cacheWriteInputTokens"]);
        return new(
            Math.Max(0, input - cached - written),
            cached,
            written,
            Number(total["outputTokens"]),
            Number(total["reasoningOutputTokens"])
        );
    }

    private async Task<JsonObject> CallAsync(string method, JsonObject parameters, CancellationToken cancellationToken)
    {
        var id = Interlocked.Increment(ref _next);
        var call = new TaskCompletionSource<JsonObject>(TaskCreationOptions.RunContinuationsAsynchronously);
        _calls[id] = call;
        await WriteAsync(
            new()
            {
                ["jsonrpc"] = "2.0",
                ["id"] = id,
                ["method"] = method,
                ["params"] = parameters,
            },
            cancellationToken
        );
        try
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            var timeout = Task.Delay(Answer, deadline.Token);
            var first = await Task.WhenAny(call.Task, Exited, timeout);
            await deadline.CancelAsync();
            if (first == call.Task)
            {
                return await call.Task;
            }
            cancellationToken.ThrowIfCancellationRequested();
            throw first == timeout
                ? new TimeoutException("codex did not answer " + method)
                : new InvalidOperationException("codex exited: " + ErrorTail);
        }
        finally
        {
            _calls.TryRemove(id, out _);
        }
    }

    private static string Version() => typeof(CodexSession).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";
}
