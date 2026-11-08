using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Channels;
using Harness.Core.Agents;
using Harness.Core.Providers;

namespace Harness.Agents;

/// <summary>
/// Reads one client's JSON-lines output for a turn: turns each line into events, and says when the turn ended.
/// </summary>
internal abstract class AgentParser
{
    public string SessionId { get; protected set; } = "";
    public string Model { get; protected set; } = "";

    /// <summary>The turn's outcome, once the client reported it ended.</summary>
    public AgentOutcome? Finished { get; protected set; }

    public abstract void Read(JsonObject line, DateTimeOffset at, List<AgentEvent> events);

    protected static string Text(JsonNode? node) =>
        node is JsonValue value && value.TryGetValue<string>(out var text) ? text : "";

    protected static long Number(JsonNode? node) =>
        node is JsonValue value && value.TryGetValue<long>(out var number) ? number : 0;

    /// <summary>A tool call's input in one short line, for the event stream.</summary>
    protected static string Short(string text)
    {
        var line = text.ReplaceLineEndings(" ").Trim();
        return line.Length <= 200 ? line : line[..199] + "…";
    }
}

/// <summary>
/// A client process running one turn. The prompt goes to its standard input, which is then closed; its standard
/// output is read line by line. The turn ends when the client says so: the process gets a moment to exit, then its
/// whole tree is stopped. Cancel stops the tree at once.
/// </summary>
internal sealed class AgentProcessRun : IAgentRun
{
    private static readonly TimeSpan Linger = TimeSpan.FromSeconds(2);
    private const int ErrorTail = 4096;

    private readonly IProviderProcess _process;
    private readonly AgentParser _parser;
    private readonly Channel<AgentEvent> _events = Channel.CreateUnbounded<AgentEvent>(
        new() { SingleReader = true, SingleWriter = true }
    );
    private readonly TaskCompletionSource<AgentOutcome> _outcome = new(
        TaskCreationOptions.RunContinuationsAsynchronously
    );
    private readonly StringBuilder _error = new();
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly Task _pump;
    private readonly Task _drain;
    private int _cancelled;

    private AgentProcessRun(string client, string taskId, IProviderProcess process, AgentParser parser)
    {
        Client = client;
        TaskId = taskId;
        _process = process;
        _parser = parser;
        _drain = DrainErrorAsync();
        _pump = PumpAsync();
    }

    public string Client { get; }
    public string TaskId { get; }
    public string SessionId => _parser.SessionId;
    public IAsyncEnumerable<AgentEvent> Events => _events.Reader.ReadAllAsync();
    public Task<AgentOutcome> Completion => _outcome.Task;

    public static async Task<IAgentRun> StartAsync(
        IProviderProcessFactory processes,
        ProviderProcessOptions options,
        string client,
        string taskId,
        string prompt,
        AgentParser parser,
        CancellationToken cancellationToken
    )
    {
        var process = processes.Start(options);
        try
        {
            await process.Input.WriteAsync(new UTF8Encoding(false).GetBytes(prompt), cancellationToken);
            await process.Input.FlushAsync(cancellationToken);
            await process.Input.DisposeAsync();
        }
        catch (IOException)
        {
            // The client exited before reading its prompt; the run reports how it ended.
        }
        catch
        {
            process.KillTree();
            await process.DisposeAsync();
            throw;
        }
        return new AgentProcessRun(client, taskId, process, parser);
    }

    public Task CancelAsync()
    {
        if (Interlocked.Exchange(ref _cancelled, 1) is 0)
        {
            _process.KillTree();
            _outcome.TrySetResult(
                new(AgentRunStatus.Cancelled, SessionId)
                {
                    Model = _parser.Model,
                    DurationMs = _clock.ElapsedMilliseconds,
                }
            );
            _events.Writer.TryComplete();
        }
        return _outcome.Task;
    }

    public async ValueTask DisposeAsync()
    {
        if (!_outcome.Task.IsCompleted)
        {
            await CancelAsync();
        }
        try
        {
            await Task.WhenAll(_pump, _drain).WaitAsync(Linger);
        }
        catch (TimeoutException)
        {
            // The pipes were left open by a descendant; the process tree is gone either way.
        }
        await _process.DisposeAsync();
    }

    private async Task PumpAsync()
    {
        try
        {
            using var reader = new StreamReader(_process.Output, new UTF8Encoding(false));
            List<AgentEvent> events = [];
            while (await reader.ReadLineAsync() is { } line)
            {
                if (Read(line, events))
                {
                    await StopAsync();
                    return;
                }
            }
            var exit = await _process.Completion;
            await _drain.WaitAsync(Linger).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
            Finish(
                new(AgentRunStatus.Failed, SessionId)
                {
                    Model = _parser.Model,
                    Error = Failure(exit),
                    DurationMs = _clock.ElapsedMilliseconds,
                }
            );
        }
        catch (Exception exception) when (exception is IOException or ObjectDisposedException)
        {
            Finish(
                new(AgentRunStatus.Failed, SessionId)
                {
                    Model = _parser.Model,
                    Error = exception.Message,
                    DurationMs = _clock.ElapsedMilliseconds,
                }
            );
        }
    }

    /// <summary>Reads one output line into events; true once the turn ended.</summary>
    private bool Read(string line, List<AgentEvent> events)
    {
        if (Volatile.Read(ref _cancelled) is not 0)
        {
            return true;
        }
        JsonObject? json;
        try
        {
            json = line.TrimStart().StartsWith('{') ? JsonNode.Parse(line) as JsonObject : null;
        }
        catch (JsonException)
        {
            json = null;
        }
        if (json is null)
        {
            return false;
        }
        events.Clear();
        _parser.Read(json, DateTimeOffset.UtcNow, events);
        foreach (var item in events)
        {
            _events.Writer.TryWrite(item);
        }
        if (_parser.Finished is not { } finished)
        {
            return false;
        }
        _events.Writer.TryWrite(new(AgentEventKind.Finished, DateTimeOffset.UtcNow) { SessionId = SessionId });
        Finish(finished.DurationMs > 0 ? finished : finished with { DurationMs = _clock.ElapsedMilliseconds });
        return true;
    }

    /// <summary>After the turn ended: the client gets a moment to exit on its own, then its tree is stopped.</summary>
    private async Task StopAsync()
    {
        try
        {
            await _process.Completion.WaitAsync(Linger);
        }
        catch (TimeoutException)
        {
            _process.KillTree();
        }
    }

    private void Finish(AgentOutcome outcome)
    {
        _outcome.TrySetResult(outcome);
        _events.Writer.TryComplete();
    }

    private string Failure(int exit)
    {
        string tail;
        lock (_error)
        {
            tail = _error.ToString().Trim();
        }
        var said = _parser.Finished?.Error ?? "";
        return (said.Length > 0 ? said : "the client exited (" + exit + ") before the turn ended")
            + (tail.Length > 0 ? ": " + tail : "");
    }

    private async Task DrainErrorAsync()
    {
        try
        {
            using var reader = new StreamReader(_process.Error, new UTF8Encoding(false));
            var buffer = new char[1024];
            int read;
            while ((read = await reader.ReadAsync(buffer)) > 0)
            {
                lock (_error)
                {
                    _error.Append(buffer, 0, read);
                    if (_error.Length > ErrorTail)
                    {
                        _error.Remove(0, _error.Length - ErrorTail);
                    }
                }
            }
        }
        catch (Exception exception) when (exception is IOException or ObjectDisposedException)
        {
            // The process tree was stopped; what was read is kept.
        }
    }
}
