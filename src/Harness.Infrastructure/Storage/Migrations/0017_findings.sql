-- Architecture findings with memory (TASK-0030). A project database holds
-- its project's findings; a workspace's knowledge database holds the
-- system findings between its members. Each sync takes a snapshot when the
-- findings differ from the last one; a finding's status says how it
-- changed in that snapshot.
CREATE TABLE finding_snapshots (
    id         INTEGER PRIMARY KEY,
    commit_sha TEXT NOT NULL DEFAULT '',
    taken_at   TEXT NOT NULL,
    found      INTEGER NOT NULL,
    added      INTEGER NOT NULL,
    changed    INTEGER NOT NULL,
    gone       INTEGER NOT NULL
) STRICT;

CREATE TABLE findings (
    key        TEXT PRIMARY KEY,      -- kind and the units it is about
    kind       TEXT NOT NULL,
    level      TEXT NOT NULL,
    title      TEXT NOT NULL,
    measure    INTEGER NOT NULL,
    finding    TEXT NOT NULL,         -- the finding as last seen (JSON)
    hash       TEXT NOT NULL,         -- what it says, to tell changed from unchanged
    status     TEXT NOT NULL CHECK (status IN ('new', 'unchanged', 'changed', 'gone')),
    snapshot   INTEGER NOT NULL REFERENCES finding_snapshots (id),
    first_seen TEXT NOT NULL,
    last_seen  TEXT NOT NULL
) STRICT;

-- The author's triage: by design (with the reason, recorded as a decision
-- memory too), back for review (a by-design finding that grew), or made a
-- task. Kept while the finding is gone, so a task's finding shows resolved.
CREATE TABLE finding_marks (
    key           TEXT PRIMARY KEY,
    state         TEXT NOT NULL CHECK (state IN ('by_design', 'review', 'task')),
    reason        TEXT NOT NULL DEFAULT '',
    level         TEXT NOT NULL,      -- as marked
    measure       INTEGER NOT NULL,   -- as marked: review when it grows
    review_reason TEXT NOT NULL DEFAULT '',
    memory_id     TEXT NOT NULL DEFAULT '',
    task_id       TEXT NOT NULL DEFAULT '',
    task_project  TEXT NOT NULL DEFAULT '',  -- the project whose database holds the task
    marked_by     TEXT NOT NULL,
    marked_at     TEXT NOT NULL
) STRICT;
