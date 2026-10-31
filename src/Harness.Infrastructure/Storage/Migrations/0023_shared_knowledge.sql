-- Project knowledge shared through Git (BP-002): .harness/knowledge.toml holds the
-- memories the team shares. Each memory records whether it is the team's: '' local,
-- 'project' in the shared file, 'removed' taken out of it by a Git change and waiting
-- for the user to keep it as local or retire it. knowledge_files keeps the hash of
-- each worktree's file as last read, so an unchanged file costs one comparison. The audit trail records sharing,
-- withdrawing, removal by a Git change and keeping a removed memory as local; SQLite
-- cannot change a CHECK constraint, so memory_events is rebuilt.
ALTER TABLE memories ADD COLUMN shared TEXT NOT NULL DEFAULT '' CHECK (shared IN ('', 'project', 'removed'));
CREATE TABLE knowledge_files (
    path    TEXT PRIMARY KEY,
    hash    TEXT NOT NULL,
    read_at TEXT NOT NULL
) STRICT;
CREATE TABLE memory_events_new (
    id         INTEGER PRIMARY KEY,
    memory_id  TEXT NOT NULL REFERENCES memories (id),
    event      TEXT NOT NULL CHECK (event IN ('created', 'superseded', 'reaffirmed', 'retired', 'shared', 'withdrawn', 'removed_from_project', 'kept_local')),
    related_id TEXT,  -- e.g. the superseding memory
    actor      TEXT NOT NULL,
    detail     TEXT,
    at         TEXT NOT NULL
) STRICT;
INSERT INTO memory_events_new (id, memory_id, event, related_id, actor, detail, at)
    SELECT id, memory_id, event, related_id, actor, detail, at FROM memory_events;
DROP TABLE memory_events;
ALTER TABLE memory_events_new RENAME TO memory_events;
CREATE INDEX memory_events_memory ON memory_events (memory_id);
