-- Blueprints agents propose (TASK-0049): a new one, or a change to an existing
-- one. Like task proposals, nothing changes until the author approves it
-- (which creates or restates the blueprint) or rejects it.
CREATE TABLE blueprint_proposals (
    id          TEXT PRIMARY KEY,
    blueprint   TEXT NOT NULL DEFAULT '',   -- the blueprint it changes; '' proposes a new one
    definition  TEXT NOT NULL,              -- JSON: the proposed title, goal, exit and notes
    reason      TEXT NOT NULL DEFAULT '',
    status      TEXT NOT NULL CHECK (status IN ('pending', 'approved', 'rejected')),
    proposed_by TEXT NOT NULL,
    proposed_at TEXT NOT NULL,
    decided_by  TEXT NOT NULL DEFAULT '',
    decided_at  TEXT NOT NULL DEFAULT '',
    note        TEXT NOT NULL DEFAULT '',
    result      TEXT NOT NULL DEFAULT ''    -- the blueprint id it created or changed
) STRICT;
CREATE INDEX blueprint_proposals_status ON blueprint_proposals (status, proposed_at);
