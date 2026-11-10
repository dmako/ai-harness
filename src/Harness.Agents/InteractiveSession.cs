using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Harness.Core.Agents;
using Harness.Core.Providers;
using Harness.Core.Tasks;

namespace Harness.Agents;

/// <summary>
/// A client session kept open across turns, steered over the client's standard input: send a turn, stop the running
/// turn, answer a tool-approval request, close. Everything it does goes to <c>emit</c> as wire events (JSON objects
/// with an "event" name), which the host logs and passes on.
/// </summary>
internal abstract class InteractiveSession : IAsyncDisposable
{
    private readonly SemaphoreSlim _write = new(1, 1);
    private readonly Action<JsonObject> _emit;
    private Task _pump = Task.CompletedTask;
    private Task _drain = Task.CompletedTask;
    private readonly StringBuilder _error = new();
    private string _announced = "";

    protected InteractiveSession(IProviderProcess process, Action<JsonObject> emit)
    {
        Process = process;
        _emit = emit;
    }

    protected IProviderProcess Process { get; }

    public abstract string Client { get; }
    public string SessionId { get; protected set; } = "";
    public string Model { get; protected set; } = "";

    /// <summary>A turn is running.</summary>
    public bool Busy { get; protected set; }

    /// <summary>Ends when the client's process has exited.</summary>
    public Task<int> Exited => Process.Completion;

    /// <summary>The end of what the client wrote to standard error, to explain how it ended.</summary>
    public string ErrorTail
    {
        get
        {
            lock (_error)
            {
                return _error.ToString().Trim();
            }
        }
    }

    /// <summary>Starts reading the client; the subclass then does its handshake.</summary>
    public async Task OpenAsync(CancellationToken cancellationToken)
    {
        _drain = DrainErrorAsync();
        _pump = PumpAsync();
        await HandshakeAsync(cancellationToken);
    }

    protected virtual Task HandshakeAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    /// <summary>Starts a turn with this text; a running turn must end first.</summary>
    public async Task SendAsync(string text, CancellationToken cancellationToken)
    {
        if (Busy)
        {
            throw new InvalidOperationException("a turn is running: stop it or wait for it to end");
        }
        Busy = true;
        Emit(new() { ["event"] = "user", ["text"] = text });
        Emit(new() { ["event"] = "status", ["status"] = "running" });
        try
        {
            await StartTurnAsync(text, cancellationToken);
        }
        catch
        {
            Busy = false;
            throw;
        }
    }

    protected abstract Task StartTurnAsync(string text, CancellationToken cancellationToken);

    /// <summary>Interrupts the running turn; the session stays open.</summary>
    public abstract Task StopAsync(CancellationToken cancellationToken);

    /// <summary>Answers the approval request <paramref name="id"/>.</summary>
    public abstract Task AnswerAsync(string id, bool allow, CancellationToken cancellationToken);

    protected abstract void Read(JsonObject line);

    protected void Emit(JsonObject item) => _emit(item);

    protected void EmitEvent(AgentEvent item)
    {
        if (item.Kind is AgentEventKind.Finished)
        {
            return;
        }
        if (item.Kind is AgentEventKind.Session)
        {
            // Claude names the session again at every turn; it is news only the first time.
            var name = item.SessionId + "/" + item.Model;
            if (name == _announced)
            {
                return;
            }
            _announced = name;
        }
        Emit(AgentHost.Event(item));
    }

    /// <summary>The end of a turn: its tokens, and how it ended.</summary>
    protected void TurnEnded(string status, TokenCounts usage, string error = "", double? cost = null, long ms = 0)
    {
        Busy = false;
        EmitEvent(
            new AgentEvent(AgentEventKind.Usage, DateTimeOffset.UtcNow)
            {
                SessionId = SessionId,
                Model = Model,
                Usage = usage,
            }
        );
        var turn = new JsonObject { ["event"] = "turn", ["status"] = status };
        if (error.Length > 0)
        {
            turn["error"] = error;
        }
        if (cost is { } dollars)
        {
            turn["cost_usd"] = dollars;
        }
        if (ms > 0)
        {
            turn["duration_ms"] = ms;
        }
        Emit(turn);
        Emit(new() { ["event"] = "status", ["status"] = "idle" });
    }

    protected void Approval(string id, string tool, string text) =>
        Emit(
            new()
            {
                ["event"] = "approval",
                ["id"] = id,
                ["tool"] = tool,
                ["text"] = text,
            }
        );

    protected void Answered(string id, bool allow) =>
        Emit(
            new()
            {
                ["event"] = "answered",
                ["id"] = id,
                ["allow"] = allow,
            }
        );

    protected async Task WriteAsync(JsonObject message, CancellationToken cancellationToken)
    {
        var bytes = new UTF8Encoding(false).GetBytes(message.ToJsonString() + "\n");
        await _write.WaitAsync(cancellationToken);
        try
        {
            await Process.Input.WriteAsync(bytes, cancellationToken);
            await Process.Input.FlushAsync(cancellationToken);
        }
        finally
        {
            _write.Release();
        }
    }

    private async Task PumpAsync()
    {
        try
        {
            using var reader = new StreamReader(Process.Output, new UTF8Encoding(false));
            while (await reader.ReadLineAsync() is { } line)
            {
                JsonObject? json = null;
                try
                {
                    json = line.TrimStart().StartsWith('{', StringComparison.Ordinal)
                        ? JsonNode.Parse(line) as JsonObject
                        : null;
                }
                catch (JsonException)
                {
                    // Not a protocol line.
                }
                if (json is not null)
                {
                    Read(json);
                }
            }
        }
        catch (Exception exception) when (exception is IOException or ObjectDisposedException)
        {
            // The process is gone; the host reports how it ended.
        }
    }

    private async Task DrainErrorAsync()
    {
        try
        {
            using var reader = new StreamReader(Process.Error, new UTF8Encoding(false));
            var buffer = new char[1024];
            int read;
            while ((read = await reader.ReadAsync(buffer)) > 0)
            {
                lock (_error)
                {
                    _error.Append(buffer, 0, read);
                    if (_error.Length > 4096)
                    {
                        _error.Remove(0, _error.Length - 4096);
                    }
                }
            }
        }
        catch (Exception exception) when (exception is IOException or ObjectDisposedException)
        {
            // The process is gone.
        }
    }

    protected static string Text(JsonNode? node) =>
        node is JsonValue value && value.TryGetValue<string>(out var text) ? text : "";

    protected static long Number(JsonNode? node) =>
        node is JsonValue value && value.TryGetValue<long>(out var number) ? number : 0;

    protected static string Short(string text)
    {
        var line = text.ReplaceLineEndings(" ").Trim();
        return line.Length <= 300 ? line : line[..299] + "…";
    }

    public async ValueTask DisposeAsync()
    {
        Process.KillTree();
        try
        {
            await Task.WhenAll(_pump, _drain).WaitAsync(TimeSpan.FromSeconds(2));
        }
        catch (TimeoutException)
        {
            // A descendant kept the pipes open; the tree is gone either way.
        }
        await Process.DisposeAsync();
        _write.Dispose();
    }
}

/// <summary>
/// Claude Code in print mode with stream-json input and output: each turn is a user message on standard input; tool
/// permissions come as control_request can_use_tool (--permission-prompt-tool stdio) and are answered with a
/// control_response; a control_request interrupt stops the turn.
/// </summary>
internal sealed class ClaudeSession : InteractiveSession
{
    private readonly Dictionary<string, JsonNode?> _pending = new(StringComparer.Ordinal);
    private readonly object _gate = new();
    private ClaudeParser _parser;
    private bool _stopping;
    private int _interrupts;

    private ClaudeSession(IProviderProcess process, Action<JsonObject> emit, string sessionId)
        : base(process, emit)
    {
        SessionId = sessionId;
        _parser = new(sessionId);
    }

    public override string Client => AgentClients.Claude;

    public static ClaudeSession Start(
        IProviderProcessFactory processes,
        AgentCommand command,
        AgentStart start,
        string resume,
        Action<JsonObject> emit
    )
    {
        var sessionId = resume.Length > 0 ? resume : Guid.NewGuid().ToString();
        List<string> arguments =
        [
            .. command.Prefix,
            "-p",
            "--input-format",
            "stream-json",
            "--output-format",
            "stream-json",
            "--verbose",
            "--permission-prompt-tool",
            "stdio",
            resume.Length > 0 ? "--resume" : "--session-id",
            sessionId,
        ];
        if (start.Model.Length > 0)
        {
            arguments.AddRange(["--model", start.Model]);
        }
        arguments.AddRange(start.Arguments);
        var process = processes.Start(
            new ProviderProcessOptions(command.Executable, arguments)
            {
                WorkingDirectory = start.WorkingDirectory,
                Environment = AgentEnvironment.For(start),
            }
        );
        return new ClaudeSession(process, emit, sessionId);
    }

    protected override Task HandshakeAsync(CancellationToken cancellationToken)
    {
        // Claude says nothing until its first turn: the session id is the one harness chose.
        EmitEvent(new AgentEvent(AgentEventKind.Session, DateTimeOffset.UtcNow) { SessionId = SessionId });
        return Task.CompletedTask;
    }

    protected override Task StartTurnAsync(string text, CancellationToken cancellationToken)
    {
        _stopping = false;
        return WriteAsync(
            new()
            {
                ["type"] = "user",
                ["message"] = new JsonObject { ["role"] = "user", ["content"] = text },
            },
            cancellationToken
        );
    }

    public override Task StopAsync(CancellationToken cancellationToken)
    {
        if (!Busy)
        {
            return Task.CompletedTask;
        }
        _stopping = true;
        return WriteAsync(
            new()
            {
                ["type"] = "control_request",
                ["request_id"] = "harness-interrupt-" + Interlocked.Increment(ref _interrupts),
                ["request"] = new JsonObject { ["subtype"] = "interrupt" },
            },
            cancellationToken
        );
    }

    public override async Task AnswerAsync(string id, bool allow, CancellationToken cancellationToken)
    {
        JsonNode? input;
        lock (_gate)
        {
            if (!_pending.Remove(id, out input))
            {
                throw new KeyNotFoundException("no approval request " + id + " is waiting");
            }
        }
        var response = allow
            ? new JsonObject { ["behavior"] = "allow", ["updatedInput"] = input?.DeepClone() ?? new JsonObject() }
            : new JsonObject { ["behavior"] = "deny", ["message"] = "Denied by the user in harness ui." };
        await WriteAsync(
            new()
            {
                ["type"] = "control_response",
                ["response"] = new JsonObject
                {
                    ["subtype"] = "success",
                    ["request_id"] = id,
                    ["response"] = response,
                },
            },
            cancellationToken
        );
        Answered(id, allow);
    }

    protected override void Read(JsonObject line)
    {
        if (Text(line["type"]) is "control_request")
        {
            Request(line);
            return;
        }
        List<AgentEvent> events = [];
        _parser.Read(line, DateTimeOffset.UtcNow, events);
        SessionId = _parser.SessionId;
        Model = _parser.Model.Length > 0 ? _parser.Model : Model;
        foreach (var item in events.Where(e => e.Kind is not AgentEventKind.Usage))
        {
            EmitEvent(item);
        }
        if (_parser.Finished is not { } finished)
        {
            return;
        }
        _parser = new(SessionId);
        lock (_gate)
        {
            _pending.Clear();
        }
        var status = finished.Status switch
        {
            _ when _stopping => "stopped",
            AgentRunStatus.Completed => "completed",
            _ => "failed",
        };
        TurnEnded(
            status,
            finished.Usage,
            status is "failed" ? finished.Error : "",
            finished.CostUsd,
            finished.DurationMs
        );
    }

    private void Request(JsonObject line)
    {
        var id = Text(line["request_id"]);
        var request = line["request"] as JsonObject;
        if (Text(request?["subtype"]) is not "can_use_tool" || id.Length is 0)
        {
            return;
        }
        var input = request!["input"];
        lock (_gate)
        {
            _pending[id] = input?.DeepClone();
        }
        var tool = Text(request["tool_name"]);
        var detail = Text(request["description"]);
        if (input is JsonObject fields && Text(fields["command"]) is { Length: > 0 } command)
        {
            detail = command;
        }
        else if (input is JsonObject named && Text(named["file_path"]) is { Length: > 0 } path)
        {
            detail = path;
        }
        Approval(id, tool, Short(detail.Length > 0 ? detail : input?.ToJsonString() ?? ""));
    }
}
