-- Agent session usage (TASK-0114), read from the clients' own transcripts: Claude Code's
-- ~/.claude/projects/<project>/<session>.jsonl (and its subagents), Codex's
-- ~/.codex/sessions/YYYY/MM/DD/rollout-*-<thread>.jsonl. Only counts, model names and
-- times are kept, never content.
--
-- agent_transcripts: how far each transcript file was read (byte offset of the last
-- complete line) and the little state the next read continues from (JSON: the last
-- message id and activity time, the current model, Codex's last cumulative totals).
-- A file shorter than its offset was rotated: its usage is dropped and read again.
CREATE TABLE agent_transcripts (
    client     TEXT NOT NULL,
    session_id TEXT NOT NULL,
    path       TEXT NOT NULL,
    offset     INTEGER NOT NULL DEFAULT 0,
    state      TEXT NOT NULL DEFAULT '{}',
    read_at    TEXT NOT NULL,
    PRIMARY KEY (client, session_id, path)
) STRICT;

-- agent_usage: a session's usage per file, minute and model, so any time window (the part
-- of a session spent on one task) sums its rows. first_at/last_at are the exact first and
-- last activity within the minute.
CREATE TABLE agent_usage (
    client        TEXT NOT NULL,
    session_id    TEXT NOT NULL,
    path          TEXT NOT NULL,
    minute        TEXT NOT NULL,            -- yyyy-MM-ddTHH:mm (UTC)
    model         TEXT NOT NULL DEFAULT '',
    input         INTEGER NOT NULL DEFAULT 0,
    cached_input  INTEGER NOT NULL DEFAULT 0,
    cache_write   INTEGER NOT NULL DEFAULT 0,
    output        INTEGER NOT NULL DEFAULT 0,
    reasoning     INTEGER NOT NULL DEFAULT 0,
    active_ms     INTEGER NOT NULL DEFAULT 0,
    first_at      TEXT NOT NULL,
    last_at       TEXT NOT NULL,
    PRIMARY KEY (client, session_id, path, minute, model)
) STRICT;

-- A task's share of each session's usage, recomputed from agent_usage on every refresh.
ALTER TABLE agent_sessions ADD COLUMN reasoning_tokens INTEGER NOT NULL DEFAULT 0;
ALTER TABLE agent_sessions ADD COLUMN active_ms INTEGER NOT NULL DEFAULT 0;
ALTER TABLE agent_sessions ADD COLUMN first_activity TEXT NOT NULL DEFAULT '';
ALTER TABLE agent_sessions ADD COLUMN last_activity TEXT NOT NULL DEFAULT '';
