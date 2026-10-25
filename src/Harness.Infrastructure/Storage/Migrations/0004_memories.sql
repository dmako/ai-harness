-- Durable, typed project memory. One short statement per row; raw chat
-- transcripts are not a supported representation.
CREATE TABLE memories (
    id               TEXT PRIMARY KEY,
    type             TEXT NOT NULL CHECK (type IN
                       ('goal', 'constraint', 'decision', 'fact', 'open_question', 'warning', 'convention')),
    content          TEXT NOT NULL CHECK (length(content) BETWEEN 1 AND 4000),
    content_hash     TEXT NOT NULL,  -- of normalized content, for exact-duplicate detection
    scope_kind       TEXT NOT NULL CHECK (scope_kind IN ('project', 'path', 'subsystem', 'branch')),
    scope_value      TEXT NOT NULL,  -- '' for project scope
    -- Provenance: who/what recorded it and where it came from.
    source_type      TEXT NOT NULL CHECK (source_type IN ('agent', 'user', 'import', 'system')),
    source_ref       TEXT NOT NULL CHECK (length(source_ref) > 0),
    created_by       TEXT NOT NULL,  -- client that stored it, e.g. "claude-code 2.1"
    confidence       REAL CHECK (confidence IS NULL OR (confidence >= 0 AND confidence <= 1)),
    superseded_by    TEXT REFERENCES memories (id) DEFERRABLE INITIALLY DEFERRED,
    supersede_reason TEXT,
    valid_from       TEXT,
    valid_until      TEXT,
    created_at       TEXT NOT NULL,
    updated_at       TEXT NOT NULL,
    CHECK ((scope_kind = 'project') = (scope_value = ''))
) STRICT;

-- At most one current memory with the same meaning in the same scope.
CREATE UNIQUE INDEX memories_current_unique
    ON memories (type, scope_kind, scope_value, content_hash) WHERE superseded_by IS NULL;
CREATE INDEX memories_current ON memories (type, scope_kind, scope_value) WHERE superseded_by IS NULL;
CREATE INDEX memories_superseded_by ON memories (superseded_by);

-- Full-text search over content (external content table kept in sync by
-- triggers).
CREATE VIRTUAL TABLE memories_fts USING fts5(
    content, content = 'memories', content_rowid = 'rowid', tokenize = 'porter unicode61');

CREATE TRIGGER memories_fts_insert AFTER INSERT ON memories BEGIN
    INSERT INTO memories_fts (rowid, content) VALUES (new.rowid, new.content);
END;
CREATE TRIGGER memories_fts_delete AFTER DELETE ON memories BEGIN
    INSERT INTO memories_fts (memories_fts, rowid, content) VALUES ('delete', old.rowid, old.content);
END;
CREATE TRIGGER memories_fts_update AFTER UPDATE OF content ON memories BEGIN
    INSERT INTO memories_fts (memories_fts, rowid, content) VALUES ('delete', old.rowid, old.content);
    INSERT INTO memories_fts (rowid, content) VALUES (new.rowid, new.content);
END;

-- Audit trail: every change to a memory's status is recorded, so nothing
-- is merged or replaced without traceability.
CREATE TABLE memory_events (
    id         INTEGER PRIMARY KEY,
    memory_id  TEXT NOT NULL REFERENCES memories (id),
    event      TEXT NOT NULL CHECK (event IN ('created', 'superseded', 'reaffirmed')),
    related_id TEXT,  -- e.g. the superseding memory
    actor      TEXT NOT NULL,
    detail     TEXT,
    at         TEXT NOT NULL
) STRICT;
CREATE INDEX memory_events_memory ON memory_events (memory_id);
