-- Deferred tasks (TASK-0015): set aside by a decision, not blocked by a
-- problem. SQLite cannot change a CHECK constraint, so tasks is rebuilt
-- with 'deferred' allowed; blocked_reason holds the reason for either.
-- Other tables reference tasks, so foreign keys are off while it runs and
-- checked (foreign_key_check) before commit.
-- harness: foreign keys off
CREATE TABLE tasks_new (
    id                 TEXT PRIMARY KEY,
    milestone          TEXT NOT NULL DEFAULT '',
    title              TEXT NOT NULL CHECK (length(title) > 0),
    type               TEXT NOT NULL DEFAULT 'feature'
                         CHECK (type IN ('feature', 'bug', 'chore', 'spike', 'docs', 'test', 'process')),
    priority           TEXT NOT NULL DEFAULT 'medium' CHECK (priority IN ('low', 'medium', 'high', 'critical')),
    status             TEXT NOT NULL
                         CHECK (status IN ('backlog', 'ready', 'claimed', 'in_progress', 'review', 'done', 'blocked', 'deferred')),
    goal               TEXT NOT NULL DEFAULT '',
    scope              TEXT NOT NULL DEFAULT '[]',
    validation         TEXT NOT NULL DEFAULT '[]',
    notes              TEXT NOT NULL DEFAULT '',
    blocked_reason     TEXT,  -- why blocked, or when to bring a deferred task back
    claimant           TEXT,
    claim_token        TEXT,
    claimed_at         TEXT,
    lease_until        TEXT,
    completion_summary TEXT,
    completion_commit  TEXT,
    completion_checks  TEXT,
    completed_by       TEXT,
    completed_at       TEXT,
    source             TEXT NOT NULL DEFAULT 'manual',
    ordinal            INTEGER NOT NULL DEFAULT 0,
    created_at         TEXT NOT NULL,
    updated_at         TEXT NOT NULL,
    CHECK ((status IN ('claimed', 'in_progress', 'review')) = (claimant IS NOT NULL)),
    CHECK ((status = 'done') = (completed_at IS NOT NULL))
) STRICT;
INSERT INTO tasks_new SELECT * FROM tasks;
DROP TABLE tasks;
ALTER TABLE tasks_new RENAME TO tasks;
CREATE INDEX tasks_status ON tasks (status, milestone);
