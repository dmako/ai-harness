-- Agents' proposals of architectural tasks and by-design marks (TASK-0032):
-- nothing changes until the author approves one (which creates the task or
-- the mark) or rejects it. Kept in the proposing project's database, also
-- for a finding between a workspace's services.
CREATE TABLE finding_proposals (
    id          TEXT PRIMARY KEY,
    kind        TEXT NOT NULL CHECK (kind IN ('task', 'by_design')),
    scope       TEXT NOT NULL,              -- 'project' or 'workspace:<name>'
    finding_key TEXT NOT NULL DEFAULT '',   -- '' for an agent's own observation
    -- basis: the finding's (fact, heuristic), or 'observation': the agent's
    -- own, a model's interpretation
    basis       TEXT NOT NULL CHECK (basis IN ('fact', 'heuristic', 'observation')),
    title       TEXT NOT NULL CHECK (length(title) > 0),
    body        TEXT NOT NULL DEFAULT '',   -- task: what should change and why; mark: why it is intended
    evidence    TEXT NOT NULL DEFAULT '[]', -- JSON list of places
    status      TEXT NOT NULL CHECK (status IN ('pending', 'approved', 'rejected')),
    proposed_by TEXT NOT NULL,
    proposed_at TEXT NOT NULL,
    decided_by  TEXT NOT NULL DEFAULT '',
    decided_at  TEXT NOT NULL DEFAULT '',
    note        TEXT NOT NULL DEFAULT '',
    result      TEXT NOT NULL DEFAULT ''    -- the task id, or 'marked'
) STRICT;
CREATE INDEX finding_proposals_status ON finding_proposals (status, proposed_at);

-- who proposed a mark the author approved
ALTER TABLE finding_marks ADD COLUMN proposed_by TEXT NOT NULL DEFAULT '';
