-- Project knowledge shared through Git (BP-002): .harness/knowledge.toml holds the
-- memories the team shares. Each memory records whether it is the team's: '' local,
-- 'project' in the shared file, 'removed' taken out of it by a Git change and waiting
-- for the user to keep it as local or retire it. knowledge_files keeps the hash of
-- each worktree's file as last read, so an unchanged file costs one comparison.
ALTER TABLE memories ADD COLUMN shared TEXT NOT NULL DEFAULT '' CHECK (shared IN ('', 'project', 'removed'));
CREATE TABLE knowledge_files (
    path    TEXT PRIMARY KEY,
    hash    TEXT NOT NULL,
    read_at TEXT NOT NULL
) STRICT;
