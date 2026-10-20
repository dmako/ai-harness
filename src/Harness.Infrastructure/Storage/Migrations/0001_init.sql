-- Diagnostic key/value store used to verify persistence end to end.
CREATE TABLE diagnostics (
    key        TEXT PRIMARY KEY,
    value      TEXT NOT NULL,
    updated_at TEXT NOT NULL
) STRICT;
