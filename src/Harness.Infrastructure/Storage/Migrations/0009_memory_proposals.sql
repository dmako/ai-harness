-- Memory proposals: candidate goals, constraints, decisions, conventions...
-- noticed during work and waiting for approval. A proposal is not active
-- memory; approving it stores a memory (possibly superseding another).
-- Like memories, one short statement each; never transcripts.
CREATE TABLE memory_proposals (
    id            TEXT PRIMARY KEY,
    type          TEXT NOT NULL CHECK (type IN
                    ('goal', 'constraint', 'decision', 'fact', 'open_question', 'warning', 'convention')),
    content       TEXT NOT NULL CHECK (length(content) BETWEEN 1 AND 4000),
    content_hash  TEXT NOT NULL,
    scope_kind    TEXT NOT NULL CHECK (scope_kind IN ('project', 'path', 'subsystem', 'branch')),
    scope_value   TEXT NOT NULL,
    source_type   TEXT NOT NULL CHECK (source_type IN ('agent', 'user', 'import', 'system')),
    source_ref    TEXT NOT NULL CHECK (length(source_ref) > 0),
    proposed_by   TEXT NOT NULL,
    rationale     TEXT,           -- why it was proposed, one line
    supersedes    TEXT,           -- memory the proposer thinks this replaces
    status        TEXT NOT NULL CHECK (status IN ('pending', 'approved', 'rejected')),
    decided_by    TEXT,
    decided_at    TEXT,
    decision_note TEXT,
    memory_id     TEXT REFERENCES memories (id),  -- the memory an approval stored or reaffirmed
    created_at    TEXT NOT NULL,
    CHECK ((scope_kind = 'project') = (scope_value = ''))
) STRICT;

CREATE INDEX memory_proposals_status ON memory_proposals (status, created_at);
-- At most one pending proposal with the same meaning in the same scope.
CREATE UNIQUE INDEX memory_proposals_pending_unique
    ON memory_proposals (type, scope_kind, scope_value, content_hash) WHERE status = 'pending';
