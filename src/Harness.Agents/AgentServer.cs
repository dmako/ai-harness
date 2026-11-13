using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Channels;
using Harness.Core.Agents;
using Harness.Core.Providers;
using Harness.Infrastructure.Agents;
using Harness.Infrastructure.Storage;
using Harness.Infrastructure.Tasks;

namespace Harness.Agents;

/// <summary>
/// harness-agents serve: one agent session kept open for a task. It listens on its named pipe (current user only):
/// a connection first gets every event so far, then each new one, and may send commands (send, stop, approve, close).
/// Events are numbered and logged beside the project database with the host's state, so harness ui can show them
/// after the host is gone. The host ends when the session is closed, the client exits, the session sat idle too long,
/// or it is stopped (Ctrl+C, SIGTERM); the client's process tree goes with it.
/// </summary>
internal sealed class AgentServer : IAsyncDisposable
{
    private const int Kept = 5000;
    private const int PipeBuffer = 64 * 1024;
    private static readonly TimeSpan Linger = TimeSpan.FromSeconds(2);

    private readonly HostOptions _options;
    private readonly Action<string> _error;
    private readonly HarnessDatabase? _database;
    private readonly object _gate = new();
    private readonly List<JsonObject> _events = [];
    private readonly HashSet<Channel<string>> _listeners = [];
    private readonly CancellationTokenSource _closing = new();
    private readonly JsonObject _state;
    private readonly StreamWriter _log;
    private InteractiveSession? _session;
    private long _sequence;
    private DateTimeOffset _lastActivity = DateTimeOffset.UtcNow;
    private string _recorded = "";

    private AgentServer(HostOptions options, Action<string> error)
    {
        AgentHostFiles.Prepare(options.State);
        // before this host's own state is written, so a resumed session finds the mode it was started with
        options = options with
        {
            Start = options.Start with { PermissionMode = PermissionMode(options) },
        };
        _options = options;
        _error = error;
        _database = options.Database.Length > 0 ? HarnessDatabase.OpenExisting(options.Database) : null;
        _log = new StreamWriter(
            new FileStream(
                AgentHostFiles.LogPath(options.State, options.Key),
                FileMode.Append,
                FileAccess.Write,
                FileShare.ReadWrite | FileShare.Delete
            ),
            new UTF8Encoding(false)
        );
        _state = new JsonObject
        {
            ["key"] = options.Key,
            ["client"] = options.Client,
            ["task_id"] = options.Start.TaskId,
            ["session_id"] = options.Resume,
            ["cwd"] = options.Start.WorkingDirectory,
            ["model"] = options.Start.Model,
            ["permission_mode"] = options.Start.PermissionMode,
            ["pid"] = Environment.ProcessId,
            ["status"] = AgentHostFiles.Starting,
            ["started_at"] = AgentHostFiles.Now(),
            ["updated_at"] = AgentHostFiles.Now(),
        };
        WriteState();
    }

    /// <summary>
    /// The session's permission mode: the one given; for a resumed session the one its newest earlier host recorded;
    /// otherwise the client's default.
    /// </summary>
    private static string PermissionMode(HostOptions options)
    {
        if (options.Start.PermissionMode.Length > 0)
        {
            return options.Start.PermissionMode;
        }
        if (options.Resume.Length > 0)
        {
            foreach (var state in AgentHostFiles.States(options.State))
            {
                if (
                    AgentHostFiles.Text(state, "client") == options.Client
                    && AgentHostFiles.Text(state, "session_id") == options.Resume
                    && AgentPermissionModes.Find(options.Client, AgentHostFiles.Text(state, "permission_mode"))
                        is { } earlier
                )
                {
                    return earlier.Id;
                }
            }
        }
        return AgentPermissionModes.Default(options.Client).Id;
    }

    public static async Task<int> ServeAsync(
        HostOptions options,
        TextWriter error,
        IProviderProcessFactory processes,
        CancellationToken lifetime
    )
    {
        await using var server = new AgentServer(options, error.WriteLine);
        return await server.RunAsync(processes, lifetime);
    }

    private async Task<int> RunAsync(IProviderProcessFactory processes, CancellationToken lifetime)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(lifetime, _closing.Token);
        var token = linked.Token;
        var command = _options.Command ?? AgentCommand.Of(_options.Client is AgentClients.Codex ? "codex" : "claude");
        try
        {
            _session =
                _options.Client is AgentClients.Codex
                    ? CodexSession.Start(processes, command, _options.Start, _options.Resume, Emit)
                    : ClaudeSession.Start(processes, command, _options.Start, _options.Resume, Emit);
            await _session.OpenAsync(token);
        }
        catch (Exception exception) when (Startup(exception))
        {
            Emit(new() { ["event"] = "error", ["text"] = Explain(exception) });
            Emit(new() { ["event"] = "status", ["status"] = AgentHostFiles.Failed });
            return AgentHost.Failed;
        }
        Emit(new() { ["event"] = "status", ["status"] = AgentHostFiles.Idle });
        var accept = AcceptAsync(token);
        if (_options.Start.Prompt.Trim().Length > 0)
        {
            await CommandAsync(new JsonObject { ["op"] = "send", ["text"] = _options.Start.Prompt }, token);
        }
        var exited = _session.Exited;
        var watch = WatchIdleAsync(token);
        var first = await Task.WhenAny(exited, watch);
        var status = AgentHostFiles.Closed;
        if (first == exited && !token.IsCancellationRequested)
        {
            var tail = _session.ErrorTail;
            Emit(
                new()
                {
                    ["event"] = "error",
                    ["text"] = "the client exited (" + await exited + ")" + (tail.Length > 0 ? ": " + tail : ""),
                }
            );
            status = AgentHostFiles.Failed;
        }
        await _closing.CancelAsync();
        if (_session.Busy)
        {
            Record("cancelled");
        }
        Emit(new() { ["event"] = "status", ["status"] = status });
        await accept.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        return status is AgentHostFiles.Closed ? AgentHost.Completed : AgentHost.Failed;
    }

    private static bool Startup(Exception exception) =>
        exception
            is IOException
                or System.ComponentModel.Win32Exception
                or InvalidOperationException
                or TimeoutException
                or OperationCanceledException;

    private static string Explain(Exception exception) =>
        exception is OperationCanceledException ? "stopped before the session opened" : exception.Message;

    /// <summary>Closes the session once it sat idle (no turn running, no command) for the configured time.</summary>
    private async Task WatchIdleAsync(CancellationToken token)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(Math.Min(30, _options.Idle.TotalSeconds / 2)));
        try
        {
            while (await timer.WaitForNextTickAsync(token))
            {
                if (_session is { Busy: false } && DateTimeOffset.UtcNow - _lastActivity > _options.Idle)
                {
                    Emit(new() { ["event"] = "error", ["text"] = "closed after sitting idle" });
                    return;
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Closed, or the host was stopped.
        }
    }

    private async Task AcceptAsync(CancellationToken token)
    {
        List<Task> connections = [];
        try
        {
            while (!token.IsCancellationRequested)
            {
                var pipe = new NamedPipeServerStream(
                    AgentHostFiles.Pipe(_options.Key),
                    PipeDirection.InOut,
                    NamedPipeServerStream.MaxAllowedServerInstances,
                    PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly,
                    // with no buffer a write waits for the reader (Windows)
                    PipeBuffer,
                    PipeBuffer
                );
                try
                {
                    await pipe.WaitForConnectionAsync(token);
                }
                catch
                {
                    await pipe.DisposeAsync();
                    throw;
                }
                connections.RemoveAll(t => t.IsCompleted);
                connections.Add(ServeAsync(pipe, token));
            }
        }
        catch (OperationCanceledException)
        {
            // Closing.
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            _error("harness-agents: the pipe failed: " + exception.Message);
            Emit(new() { ["event"] = "error", ["text"] = "the pipe failed: " + exception.Message });
        }
        await Task.WhenAll(connections).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
    }

    /// <summary>One pipe connection: the events so far and as they come, and its commands.</summary>
    private async Task ServeAsync(NamedPipeServerStream pipe, CancellationToken token)
    {
        var outgoing = Channel.CreateUnbounded<string>(new() { SingleReader = true });
        lock (_gate)
        {
            foreach (var item in _events)
            {
                outgoing.Writer.TryWrite(item.ToJsonString());
            }
            if (token.IsCancellationRequested)
            {
                outgoing.Writer.TryComplete();
            }
            else
            {
                _listeners.Add(outgoing);
            }
        }
        await using (pipe)
        {
            // once the host closes, a connection that does not take its last events in a moment is cut off: a
            // write to a reader that stopped reading would otherwise hold the host open
            using var abort = token.Register(() => _ = CutAsync(pipe));
            using var stop = CancellationTokenSource.CreateLinkedTokenSource(token);
            var reading = ReadCommandsAsync(pipe, stop.Token);
            try
            {
                var bytes = new UTF8Encoding(false);
                await foreach (var line in outgoing.Reader.ReadAllAsync(CancellationToken.None))
                {
                    await pipe.WriteAsync(bytes.GetBytes(line + "\n"), CancellationToken.None);
                    await pipe.FlushAsync(CancellationToken.None);
                }
            }
            catch (Exception exception) when (exception is IOException or ObjectDisposedException)
            {
                // The other side went away, or was cut off.
            }
            finally
            {
                lock (_gate)
                {
                    _listeners.Remove(outgoing);
                }
                await stop.CancelAsync();
                // a cancelled read on a named pipe does not always return (Windows): closing the pipe ends it
                await pipe.DisposeAsync();
                await reading.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
            }
        }
    }

    private static async Task CutAsync(NamedPipeServerStream pipe)
    {
        await Task.Delay(Linger, CancellationToken.None);
        await pipe.DisposeAsync();
    }

    private async Task ReadCommandsAsync(NamedPipeServerStream pipe, CancellationToken token)
    {
        using var reader = new StreamReader(pipe, new UTF8Encoding(false), false, 4096, leaveOpen: true);
        while (await reader.ReadLineAsync(token) is { } line)
        {
            JsonObject? command = null;
            try
            {
                command = JsonNode.Parse(line) as JsonObject;
            }
            catch (JsonException)
            {
                // Not a command.
            }
            if (command is not null)
            {
                await CommandAsync(command, token);
            }
        }
    }

    /// <summary>Carries out a command; what goes wrong is told as an error event, never ends the host.</summary>
    private async Task CommandAsync(JsonObject command, CancellationToken token)
    {
        _lastActivity = DateTimeOffset.UtcNow;
        var session = _session!;
        try
        {
            switch (AgentHostFiles.Text(command, "op"))
            {
                case "send" when AgentHostFiles.Text(command, "text").Trim() is { Length: > 0 } text:
                    await session.SendAsync(text, token);
                    break;
                case "stop":
                    await session.StopAsync(token);
                    break;
                case "approve":
                    var allow = command["allow"] is JsonValue value && value.TryGetValue<bool>(out var yes) && yes;
                    await session.AnswerAsync(AgentHostFiles.Text(command, "id"), allow, token);
                    break;
                case "close":
                    if (session.Busy)
                    {
                        await session.StopAsync(token).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
                    }
                    await _closing.CancelAsync();
                    break;
                default:
                    Emit(new() { ["event"] = "error", ["text"] = "unknown command" });
                    break;
            }
        }
        catch (Exception exception)
            when (exception is InvalidOperationException or KeyNotFoundException or IOException or TimeoutException
                && !token.IsCancellationRequested
            )
        {
            Emit(new() { ["event"] = "error", ["text"] = exception.Message });
        }
    }

    /// <summary>Numbers, logs and passes on one event, and keeps the state and the task's session row current.</summary>
    private void Emit(JsonObject item)
    {
        // the state and the session row first, so a client that sees the event (a turn's end) reads them current
        bool final;
        lock (_state)
        {
            final = Track(item);
        }
        string line;
        lock (_gate)
        {
            item["seq"] = ++_sequence;
            item["at"] ??= AgentHostFiles.Now();
            line = item.ToJsonString();
            _events.Add(item);
            if (_events.Count > Kept)
            {
                _events.RemoveRange(0, _events.Count - Kept);
            }
            foreach (var listener in _listeners)
            {
                listener.Writer.TryWrite(line);
            }
            try
            {
                _log.WriteLine(line);
                _log.Flush();
            }
            catch (IOException exception)
            {
                _error("harness-agents: could not log an event: " + exception.Message);
            }
            if (final)
            {
                // after the final status reached them: the listeners' streams end here
                foreach (var listener in _listeners)
                {
                    listener.Writer.TryComplete();
                }
                _listeners.Clear();
            }
        }
    }

    /// <summary>Applies an event to the state file and the session row; true for a final status (closed, failed).</summary>
    private bool Track(JsonObject item)
    {
        switch (AgentHostFiles.Text(item, "event"))
        {
            case "session":
                _state["session_id"] = AgentHostFiles.Text(item, "session_id");
                if (AgentHostFiles.Text(item, "model") is { Length: > 0 } model)
                {
                    _state["model"] = model;
                }
                WriteState();
                Record(AgentHostFiles.Idle);
                break;
            case "status":
                var status = AgentHostFiles.Text(item, "status");
                _state["status"] = status;
                WriteState();
                if (status is AgentHostFiles.Running)
                {
                    // before the turn starts, so a quick turn's end is recorded after it
                    Record("working");
                }
                return status is AgentHostFiles.Closed or AgentHostFiles.Failed;
            case "turn":
                _lastActivity = DateTimeOffset.UtcNow;
                Record(
                    AgentHostFiles.Text(item, "status") switch
                    {
                        "stopped" => "cancelled",
                        "failed" => "failed",
                        _ => "idle",
                    }
                );
                break;
            default:
                break;
        }
        return false;
    }

    private void WriteState()
    {
        _state["updated_at"] = AgentHostFiles.Now();
        try
        {
            AgentHostFiles.WriteState(_options.State, _options.Key, _state);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            _error("harness-agents: could not write the state: " + exception.Message);
        }
    }

    /// <summary>The task's agent_sessions row (origin harness), once the session id is known.</summary>
    private void Record(string status)
    {
        var sessionId = AgentHostFiles.Text(_state, "session_id");
        if (_database is null || sessionId.Length is 0)
        {
            return;
        }
        try
        {
            if (
                !new TaskStore(_database).RecordRun(
                    _options.Client,
                    sessionId,
                    _options.Start.TaskId,
                    status,
                    DateTimeOffset.UtcNow,
                    CancellationToken.None
                )
                && _recorded != sessionId
            )
            {
                _error("harness-agents: task " + _options.Start.TaskId + " is not in the project database");
            }
            _recorded = sessionId;
        }
        catch (Exception exception)
            when (exception is IOException or Microsoft.Data.Sqlite.SqliteException or InvalidOperationException)
        {
            _error("harness-agents: could not record the session: " + exception.Message);
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_session is not null)
        {
            await _session.DisposeAsync();
        }
        lock (_gate)
        {
            _log.Dispose();
        }
        _database?.Dispose();
        _closing.Dispose();
    }
}
