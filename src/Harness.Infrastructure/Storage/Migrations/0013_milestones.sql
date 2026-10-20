-- Milestones of the task queue: what each one is for and when it is done.
-- Definitions only, imported from the backlog file with the tasks; progress
-- is computed from the tasks.
CREATE TABLE milestones (
    id         TEXT PRIMARY KEY,           -- e.g. M16, CHK-A
    title      TEXT NOT NULL DEFAULT '',
    goal       TEXT NOT NULL DEFAULT '',
    exit       TEXT NOT NULL DEFAULT '[]', -- JSON array of exit criteria
    notes      TEXT NOT NULL DEFAULT '',
    source     TEXT NOT NULL DEFAULT 'manual',
    ordinal    INTEGER NOT NULL DEFAULT 0,
    updated_at TEXT NOT NULL
) STRICT;
