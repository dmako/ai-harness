-- Tasks agents propose (TASK-0046): an agent never creates a task; its
-- proposal waits in Work › Inbox until the author approves it (which
-- creates the task) or rejects it.
CREATE TABLE task_proposals (
    id          TEXT PRIMARY KEY,
    definition  TEXT NOT NULL,              -- JSON: the task as a backlog file writes it, without its id
    reason      TEXT NOT NULL DEFAULT '',   -- why the agent proposes it
    status      TEXT NOT NULL CHECK (status IN ('pending', 'approved', 'rejected')),
    proposed_by TEXT NOT NULL,
    proposed_at TEXT NOT NULL,
    decided_by  TEXT NOT NULL DEFAULT '',
    decided_at  TEXT NOT NULL DEFAULT '',
    note        TEXT NOT NULL DEFAULT '',   -- the author's, on rejecting
    result      TEXT NOT NULL DEFAULT ''    -- the task id it became
) STRICT;
CREATE INDEX task_proposals_status ON task_proposals (status, proposed_at);
