-- Incremental sync bookkeeping (HAR-0034).

-- Paths that differed from HEAD at the last sync: re-checked next time so
-- a reverted edit is noticed even though it no longer differs from base.
ALTER TABLE sync_state ADD COLUMN pending TEXT NOT NULL DEFAULT '[]';
-- Set when files changed but a provider refresh failed; retried next sync.
ALTER TABLE sync_state ADD COLUMN providers_stale INTEGER NOT NULL DEFAULT 0;

-- One row per sync run, for observability and failure diagnosis.
CREATE TABLE sync_runs (
    id          INTEGER PRIMARY KEY,
    worktree_id TEXT NOT NULL REFERENCES worktrees (id),
    started_at  TEXT NOT NULL,
    finished_at TEXT,
    mode        TEXT NOT NULL,          -- incremental | full | since
    reason      TEXT,                   -- why a full rescan was used
    base_commit TEXT,
    head_commit TEXT,
    added       INTEGER NOT NULL DEFAULT 0,
    changed     INTEGER NOT NULL DEFAULT 0,
    removed     INTEGER NOT NULL DEFAULT 0,
    unchanged   INTEGER NOT NULL DEFAULT 0,
    hashed      INTEGER NOT NULL DEFAULT 0,
    providers   TEXT NOT NULL DEFAULT '[]', -- JSON [{name, status, detail}]
    status      TEXT NOT NULL CHECK (status IN ('running', 'ok', 'partial', 'failed')),
    error       TEXT
) STRICT;
CREATE INDEX sync_runs_worktree ON sync_runs (worktree_id, id);
