using Harness.Core.Hooks;
using Harness.Core.Memory;
using Harness.Core.Tasks;
using Harness.Infrastructure.Storage;

namespace Harness.Infrastructure.Tasks;

public sealed partial class TaskStore
{
    /// <summary>
    /// Records that an agent session worked on a task, from a hook event of a harness task call: one row per task
    /// and session, whose first and last event give started_at and ended_at and whose latest call gives the status.
    /// A task id this project does not have (another project's, a mistyped one) is ignored; returns whether a row
    /// was written.
    /// </summary>
    public bool RecordSession(
        string client,
        string sessionId,
        string taskId,
        string action,
        DateTimeOffset at,
        CancellationToken cancellationToken = default
    )
    {
        var status = action switch
        {
            HookTaskActions.Claim or HookTaskActions.Update => "working",
            HookTaskActions.Block => "blocked",
            HookTaskActions.Release => "released",
            HookTaskActions.Complete => "completed",
            _ => "",
        };
        if (client.Length is 0 || sessionId.Length is 0 || taskId.Length is 0 || status.Length is 0)
        {
            return false;
        }
        return database.WithTransaction(
            (c, t) =>
            {
                using var command = Command(
                    c,
                    t,
                    """
                    INSERT INTO agent_sessions(task_id,client,session_id,origin,started_at,ended_at,status)
                    SELECT id,$client,$session,$origin,$at,$at,$status FROM tasks WHERE id=$task COLLATE NOCASE
                    ORDER BY id=$task DESC LIMIT 1
                    ON CONFLICT(task_id,client,session_id) DO UPDATE SET
                        started_at=min(started_at,excluded.started_at),
                        status=CASE WHEN excluded.ended_at>=ended_at THEN excluded.status ELSE status END,
                        ended_at=max(ended_at,excluded.ended_at)
                    """,
                    ("$client", client),
                    ("$session", sessionId),
                    ("$origin", AgentSession.HookOrigin),
                    ("$at", HarnessDatabase.FormatTime(at)),
                    ("$status", status),
                    ("$task", taskId)
                );
                return command.ExecuteNonQuery() > 0;
            },
            cancellationToken
        );
    }

    /// <summary>
    /// Records a session harness's agent runner started or resumed for a task (origin harness): "working" while a
    /// turn runs, then how the turn ended (idle, cancelled, failed). The end of a turn does not hide what the session
    /// did to the task meanwhile (blocked, released, completed through its own task calls). Returns whether a row was
    /// written: false for a task this project does not have.
    /// </summary>
    public bool RecordRun(
        string client,
        string sessionId,
        string taskId,
        string status,
        DateTimeOffset at,
        CancellationToken cancellationToken = default
    )
    {
        if (client.Length is 0 || sessionId.Length is 0 || taskId.Length is 0 || status.Length is 0)
        {
            return false;
        }
        return database.WithTransaction(
            (c, t) =>
            {
                using var command = Command(
                    c,
                    t,
                    """
                    INSERT INTO agent_sessions(task_id,client,session_id,origin,started_at,ended_at,status)
                    SELECT id,$client,$session,$origin,$at,$at,$status FROM tasks WHERE id=$task COLLATE NOCASE
                    ORDER BY id=$task DESC LIMIT 1
                    ON CONFLICT(task_id,client,session_id) DO UPDATE SET
                        origin=excluded.origin,
                        started_at=min(started_at,excluded.started_at),
                        status=CASE WHEN excluded.status='working'
                            OR status IN ('working','idle','cancelled','failed')
                            THEN excluded.status ELSE status END,
                        ended_at=max(ended_at,excluded.ended_at)
                    """,
                    ("$client", client),
                    ("$session", sessionId),
                    ("$origin", AgentSession.HarnessOrigin),
                    ("$at", HarnessDatabase.FormatTime(at)),
                    ("$status", status),
                    ("$task", taskId)
                );
                return command.ExecuteNonQuery() > 0;
            },
            cancellationToken
        );
    }

    /// <summary>The agent sessions that worked on a task, the first started first.</summary>
    public IReadOnlyList<AgentSession> Sessions(string id, CancellationToken cancellationToken = default) =>
        database.ReadTransaction(
            (c, t) =>
            {
                using var command = Command(
                    c,
                    t,
                    """
                    SELECT client,session_id,origin,started_at,ended_at,status,model,transcript_path,input_tokens,
                        output_tokens,cache_read_tokens,cache_write_tokens,cost_usd,reasoning_tokens,active_ms,
                        first_activity,last_activity
                    FROM agent_sessions WHERE task_id=$id ORDER BY started_at,id
                    """,
                    ("$id", id)
                );
                using var reader = command.ExecuteReader();
                List<AgentSession> sessions = [];
                while (reader.Read())
                {
                    sessions.Add(
                        new(
                            id,
                            reader.GetString(0),
                            reader.GetString(1),
                            reader.GetString(2),
                            MemoryTimestamp.Parse(reader.GetString(3)),
                            MemoryTimestamp.Parse(reader.GetString(4)),
                            reader.GetString(5)
                        )
                        {
                            Model = reader.GetString(6),
                            TranscriptPath = reader.GetString(7),
                            InputTokens = reader.GetInt64(8),
                            OutputTokens = reader.GetInt64(9),
                            CacheReadTokens = reader.GetInt64(10),
                            CacheWriteTokens = reader.GetInt64(11),
                            CostUsd = reader.IsDBNull(12) ? null : reader.GetDouble(12),
                            ReasoningTokens = reader.GetInt64(13),
                            ActiveMs = reader.GetInt64(14),
                            FirstActivity = reader.GetString(15),
                            LastActivity = reader.GetString(16),
                        }
                    );
                }
                return (IReadOnlyList<AgentSession>)sessions;
            },
            cancellationToken
        );
}
