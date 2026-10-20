-- One row per downstream provider call, for observability (latency, size,
-- failures). Pruned to the most recent rows by the writer.
CREATE TABLE provider_calls (
    id             INTEGER PRIMARY KEY,
    at             TEXT NOT NULL,
    provider       TEXT NOT NULL,
    capability     TEXT NOT NULL,
    query          TEXT NOT NULL,
    items          INTEGER NOT NULL,
    response_bytes INTEGER NOT NULL,
    duration_ms    INTEGER NOT NULL,
    error          TEXT
) STRICT;

CREATE INDEX provider_calls_provider_at ON provider_calls (provider, at);
