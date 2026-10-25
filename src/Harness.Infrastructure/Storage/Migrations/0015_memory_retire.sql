-- Retiring a memory (HAR-0116): its validity ends now and the audit trail
-- says who retired it and why; nothing is deleted. SQLite cannot change a
-- CHECK constraint, so memory_events is rebuilt with 'retired' allowed.
CREATE TABLE memory_events_new (
    id         INTEGER PRIMARY KEY,
    memory_id  TEXT NOT NULL REFERENCES memories (id),
    event      TEXT NOT NULL CHECK (event IN ('created', 'superseded', 'reaffirmed', 'retired')),
    related_id TEXT,  -- e.g. the superseding memory
    actor      TEXT NOT NULL,
    detail     TEXT,
    at         TEXT NOT NULL
) STRICT;
INSERT INTO memory_events_new (id, memory_id, event, related_id, actor, detail, at)
    SELECT id, memory_id, event, related_id, actor, detail, at FROM memory_events;
DROP TABLE memory_events;
ALTER TABLE memory_events_new RENAME TO memory_events;
CREATE INDEX memory_events_memory ON memory_events (memory_id);
