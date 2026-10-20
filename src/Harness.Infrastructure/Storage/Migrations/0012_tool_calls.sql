-- Tool and provider metrics (HAR-0150): which MCP tools agents use and how
-- they fare. Pruned to the most recent rows by the writer; local only.
CREATE TABLE tool_calls (
    id             INTEGER PRIMARY KEY,
    at             TEXT NOT NULL,
    tool           TEXT NOT NULL,
    kind           TEXT NOT NULL DEFAULT '',  -- e.g. search_project's kind
    client         TEXT NOT NULL DEFAULT '',  -- MCP client name (claude-code, codex...)
    duration_ms    INTEGER NOT NULL,
    response_bytes INTEGER NOT NULL,
    empty          INTEGER NOT NULL,          -- answered, but with nothing in it
    error          TEXT
) STRICT;
CREATE INDEX tool_calls_at ON tool_calls (at);

-- Provider calls that ran out of time, apart from other errors.
ALTER TABLE provider_calls ADD COLUMN timeout INTEGER NOT NULL DEFAULT 0;
