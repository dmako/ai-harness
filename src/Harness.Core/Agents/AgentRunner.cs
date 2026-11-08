using Harness.Core.Tasks;

namespace Harness.Core.Agents;

/// <summary>The agent clients harness can run, by the names its hooks and agent_sessions use.</summary>
public static class AgentClients
{
    public const string Claude = "claude-code";
    public const string Codex = "codex";
}

/// <summary>
/// One turn for an agent on a task: the prompt, the folder it works in and what the client should use. Arguments are
/// passed to the client as they are (a permission mode, a sandbox); Environment is added to the inherited one.
/// </summary>
public sealed record AgentStart(string TaskId, string WorkingDirectory, string Prompt)
{
    public string Model { get; init; } = "";
    public IReadOnlyList<string> Arguments { get; init; } = [];
    public IReadOnlyDictionary<string, string> Environment { get; init; } = new Dictionary<string, string>();
}

/// <summary>Starts and resumes an agent client's sessions. Cancel a run through <see cref="IAgentRun.CancelAsync"/>.</summary>
public interface IAgentRunner
{
    /// <summary>The client this runner starts (<see cref="AgentClients"/>).</summary>
    string Client { get; }

    /// <summary>Starts a new session with the start's prompt; returns once the client's process runs.</summary>
    Task<IAgentRun> StartAsync(AgentStart start, CancellationToken cancellationToken = default);

    /// <summary>Continues the session <paramref name="sessionId"/> with another turn.</summary>
    Task<IAgentRun> ResumeAsync(string sessionId, AgentStart start, CancellationToken cancellationToken = default);
}

/// <summary>
/// A running turn. Its events arrive in order and end when the turn does; Completion ends as soon as the client reports
/// the turn finished, even when its process lingers, and then the process is stopped.
/// </summary>
public interface IAgentRun : IAsyncDisposable
{
    string Client { get; }
    string TaskId { get; }

    /// <summary>The session id: chosen up front, or empty until the client reports it (a Session event).</summary>
    string SessionId { get; }

    /// <summary>The events, for one reader.</summary>
    IAsyncEnumerable<AgentEvent> Events { get; }

    Task<AgentOutcome> Completion { get; }

    /// <summary>Stops the turn and every process the client started; the session can be resumed afterwards.</summary>
    Task CancelAsync();
}

public enum AgentEventKind
{
    /// <summary>The session id is known (SessionId), with the model when the client names it.</summary>
    Session,

    /// <summary>Text the agent wrote (Text).</summary>
    Text,

    /// <summary>The agent called a tool (Tool, with a short Text such as the command).</summary>
    ToolCall,

    /// <summary>Tokens used so far in this turn's step (Usage, Model).</summary>
    Usage,

    /// <summary>The turn ended; the run's outcome follows in Completion.</summary>
    Finished,
}

public sealed record AgentEvent(AgentEventKind Kind, DateTimeOffset At)
{
    public string SessionId { get; init; } = "";
    public string Text { get; init; } = "";
    public string Tool { get; init; } = "";
    public string Model { get; init; } = "";
    public TokenCounts? Usage { get; init; }
}

public enum AgentRunStatus
{
    Completed,
    Failed,
    Cancelled,
}

/// <summary>
/// How a turn ended: its final answer (Text) or error, the tokens the client reported for the turn, and how long it
/// ran. CostUsd is the client's own API-equivalent figure, when it computes one: Claude Code reports the session's
/// total so far, so a resumed turn's figure includes the earlier turns.
/// </summary>
public sealed record AgentOutcome(AgentRunStatus Status, string SessionId)
{
    public string Text { get; init; } = "";
    public string Error { get; init; } = "";
    public string Model { get; init; } = "";
    public TokenCounts Usage { get; init; } = TokenCounts.Zero;
    public double? CostUsd { get; init; }
    public long DurationMs { get; init; }
}
