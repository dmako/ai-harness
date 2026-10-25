-- Client hook events (HAR-0151): what the agent did around harness, from
-- the opt-in Claude Code / Codex PostToolUse hook. Tool names, MCP
-- server/tool and repository-relative paths only: never contents or
-- prompts. Pruned to the most recent rows by the writer; local only.
CREATE TABLE tool_events (
    id          INTEGER PRIMARY KEY,
    at          TEXT NOT NULL,
    client      TEXT NOT NULL,                -- claude-code | codex
    session_id  TEXT NOT NULL DEFAULT '',
    worktree_id TEXT NOT NULL DEFAULT '',
    tool        TEXT NOT NULL,
    kind        TEXT NOT NULL,                -- read | search | edit | mcp | other
    mcp_server  TEXT NOT NULL DEFAULT '',
    mcp_tool    TEXT NOT NULL DEFAULT '',
    path        TEXT NOT NULL DEFAULT ''      -- repository-relative; '' when none
) STRICT;
CREATE INDEX tool_events_session ON tool_events (session_id, at);
CREATE INDEX tool_events_at ON tool_events (at);

-- The agent session a get_context answer was served in (set by the hook
-- event of that call), for read-after-context relevance.
ALTER TABLE context_calls ADD COLUMN session_id TEXT;
ALTER TABLE context_calls ADD COLUMN client TEXT;
