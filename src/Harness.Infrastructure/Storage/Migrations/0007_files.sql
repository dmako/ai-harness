-- Content hashes of synchronized files, per worktree (HAR-0033).
CREATE TABLE files (
    worktree_id TEXT NOT NULL REFERENCES worktrees (id),
    path        TEXT NOT NULL,     -- relative to the worktree root, '/'-separated
    hash        TEXT NOT NULL,     -- sha256 of the content (or of the link target for symlinks)
    size        INTEGER NOT NULL,
    mtime_ns    INTEGER NOT NULL,
    updated_at  TEXT NOT NULL,
    PRIMARY KEY (worktree_id, path)
) STRICT;
