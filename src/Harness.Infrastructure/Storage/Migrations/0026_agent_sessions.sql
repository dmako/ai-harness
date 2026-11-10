-- Agent sessions per task (TASK-0113): which Claude Code or Codex session worked on a task.
-- origin 'hook': seen through harness's PostToolUse hook claiming, updating or completing
-- the task (HookIngestion); 'harness': started by harness's own runner. One row per task
-- and session, so a task has many sessions and a session may move between tasks.
-- started_at/ended_at are the first and last event seen; status is what the session's
-- last call did: working | blocked | released | completed. Model, transcript and usage
-- are filled from the clients' transcripts later; cost_usd stays NULL when unknown.
CREATE TABLE agent_sessions (
    id                 INTEGER PRIMARY KEY,
    task_id            TEXT NOT NULL,
    client             TEXT NOT NULL,                 -- claude-code | codex
    session_id         TEXT NOT NULL,
    origin             TEXT NOT NULL CHECK (origin IN ('harness', 'hook')),
    started_at         TEXT NOT NULL,
    ended_at           TEXT NOT NULL,
    status             TEXT NOT NULL DEFAULT 'working',
    model              TEXT NOT NULL DEFAULT '',
    transcript_path    TEXT NOT NULL DEFAULT '',
    input_tokens       INTEGER NOT NULL DEFAULT 0,
    output_tokens      INTEGER NOT NULL DEFAULT 0,
    cache_read_tokens  INTEGER NOT NULL DEFAULT 0,
    cache_write_tokens INTEGER NOT NULL DEFAULT 0,
    cost_usd           REAL,
    UNIQUE (task_id, client, session_id)
) STRICT;
CREATE INDEX agent_sessions_session ON agent_sessions (client, session_id);
