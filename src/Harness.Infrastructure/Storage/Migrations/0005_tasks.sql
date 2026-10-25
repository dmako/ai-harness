-- Agent-readable task queue.
CREATE TABLE tasks (
    id                 TEXT PRIMARY KEY,  -- e.g. HAR-0024, TASK-0001
    milestone          TEXT NOT NULL DEFAULT '',
    title              TEXT NOT NULL CHECK (length(title) > 0),
    type               TEXT NOT NULL DEFAULT 'feature'
                         CHECK (type IN ('feature', 'bug', 'chore', 'spike', 'docs', 'test', 'process')),
    priority           TEXT NOT NULL DEFAULT 'medium' CHECK (priority IN ('low', 'medium', 'high', 'critical')),
    status             TEXT NOT NULL
                         CHECK (status IN ('backlog', 'ready', 'claimed', 'in_progress', 'review', 'done', 'blocked')),
    goal               TEXT NOT NULL DEFAULT '',
    scope              TEXT NOT NULL DEFAULT '[]',  -- JSON array of paths
    validation         TEXT NOT NULL DEFAULT '[]',  -- JSON array of commands
    notes              TEXT NOT NULL DEFAULT '',
    blocked_reason     TEXT,
    -- Claim and lease.
    claimant           TEXT,
    claim_token        TEXT,
    claimed_at         TEXT,
    lease_until        TEXT,
    -- Completion evidence.
    completion_summary TEXT,
    completion_commit  TEXT,
    completion_checks  TEXT,  -- JSON array of {command, result, output}
    completed_by       TEXT,
    completed_at       TEXT,
    -- Bookkeeping.
    source             TEXT NOT NULL DEFAULT 'manual',  -- manual | import:<file>
    ordinal            INTEGER NOT NULL DEFAULT 0,      -- backlog order, for deterministic ranking
    created_at         TEXT NOT NULL,
    updated_at         TEXT NOT NULL,
    CHECK ((status IN ('claimed', 'in_progress', 'review')) = (claimant IS NOT NULL)),
    CHECK ((status = 'done') = (completed_at IS NOT NULL))
) STRICT;
CREATE INDEX tasks_status ON tasks (status, milestone);

-- Normalized dependency edges: task_id depends on depends_on.
CREATE TABLE task_dependencies (
    task_id    TEXT NOT NULL REFERENCES tasks (id),
    depends_on TEXT NOT NULL REFERENCES tasks (id),
    PRIMARY KEY (task_id, depends_on),
    CHECK (task_id <> depends_on)
) STRICT;
CREATE INDEX task_dependencies_reverse ON task_dependencies (depends_on);

-- Acceptance criteria and their reported results.
CREATE TABLE task_acceptance (
    task_id   TEXT NOT NULL REFERENCES tasks (id),
    idx       INTEGER NOT NULL,  -- 1-based
    criterion TEXT NOT NULL,
    result    TEXT NOT NULL DEFAULT 'pending' CHECK (result IN ('pending', 'pass', 'fail', 'n/a')),
    evidence  TEXT,
    PRIMARY KEY (task_id, idx)
) STRICT;

-- Audit trail of every state change.
CREATE TABLE task_events (
    id          INTEGER PRIMARY KEY,
    task_id     TEXT NOT NULL REFERENCES tasks (id),
    event       TEXT NOT NULL,
    from_status TEXT,
    to_status   TEXT,
    actor       TEXT NOT NULL,
    detail      TEXT,
    at          TEXT NOT NULL
) STRICT;
CREATE INDEX task_events_task ON task_events (task_id);
