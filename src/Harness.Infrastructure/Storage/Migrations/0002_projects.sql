-- A project database belongs to exactly one project.
CREATE TABLE projects (
    id           TEXT PRIMARY KEY,
    singleton    INTEGER NOT NULL DEFAULT 1 UNIQUE CHECK (singleton = 1),
    root_path    TEXT NOT NULL,  -- last known state root (main working tree)
    db_path      TEXT NOT NULL,  -- last known absolute database path
    copied_from  TEXT,           -- project id this copy was made from
    created_at   TEXT NOT NULL,
    updated_at   TEXT NOT NULL
) STRICT;

-- Working trees (main + linked) sharing the project database. Keyed by the
-- per-worktree git dir, which is stable across 'git worktree move'.
CREATE TABLE worktrees (
    id            TEXT PRIMARY KEY,
    project_id    TEXT NOT NULL REFERENCES projects(id),
    git_dir       TEXT NOT NULL UNIQUE,
    path          TEXT NOT NULL,
    linked        INTEGER NOT NULL,
    branch        TEXT NOT NULL,   -- '' when detached
    head_commit   TEXT NOT NULL,   -- '' when unborn
    detached      INTEGER NOT NULL,
    first_seen_at TEXT NOT NULL,
    last_seen_at  TEXT NOT NULL
) STRICT;
