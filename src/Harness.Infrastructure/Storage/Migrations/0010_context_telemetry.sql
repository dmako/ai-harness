-- Context telemetry (HAR-0144): what get_context returned, and whether the
-- work that followed touched it. Local only; retention is bounded by the
-- telemetry package.
CREATE TABLE context_calls (
    id          INTEGER PRIMARY KEY,
    at          TEXT NOT NULL,
    worktree_id TEXT NOT NULL,
    head        TEXT NOT NULL,        -- HEAD commit when the context was served
    task        TEXT NOT NULL CHECK (length(task) <= 2000),
    budget      INTEGER NOT NULL,
    used        INTEGER NOT NULL,
    resolved_at TEXT,                 -- when outcomes were joined
    resolved_by TEXT,                 -- sync or task
    outcome_to  TEXT                  -- commit the outcome was measured at
) STRICT;
CREATE INDEX context_calls_open ON context_calls (worktree_id, resolved_at);

-- One row per returned code unit and reporting provider.
CREATE TABLE context_units (
    call_id    INTEGER NOT NULL REFERENCES context_calls (id) ON DELETE CASCADE,
    path       TEXT NOT NULL,
    name       TEXT NOT NULL,
    role       TEXT NOT NULL,
    provider   TEXT NOT NULL,
    capability TEXT NOT NULL,
    rank       INTEGER NOT NULL,      -- position in that provider's answer
    position   INTEGER NOT NULL,      -- position in the returned context
    tier       INTEGER NOT NULL,
    inlined    INTEGER NOT NULL,
    language   TEXT NOT NULL
) STRICT;
CREATE INDEX context_units_call ON context_units (call_id);

-- Files the following work changed, and whether the context contained them.
CREATE TABLE context_outcomes (
    call_id  INTEGER NOT NULL REFERENCES context_calls (id) ON DELETE CASCADE,
    path     TEXT NOT NULL,
    language TEXT NOT NULL,
    hit      INTEGER NOT NULL
) STRICT;
CREATE INDEX context_outcomes_call ON context_outcomes (call_id);
