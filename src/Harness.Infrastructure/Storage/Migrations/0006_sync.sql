-- Git-aware synchronization state (M5).

-- Last synchronized state per worktree (HAR-0031).
CREATE TABLE sync_state (
    worktree_id  TEXT PRIMARY KEY REFERENCES worktrees (id),
    branch       TEXT NOT NULL,    -- '' when detached
    head_commit  TEXT NOT NULL,    -- '' when unborn
    detached     INTEGER NOT NULL,
    dirty        INTEGER NOT NULL, -- uncommitted changes at sync time
    dirty_paths  INTEGER NOT NULL,
    synced_at    TEXT NOT NULL
) STRICT;
