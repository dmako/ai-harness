-- The order the author chose for the blueprints in harness ui (TASK-0078), apart
-- from blueprints.ordinal, which is the backlog's order and set by every import.
-- A row per ordered blueprint, described or only named by tasks; blueprints
-- without one are listed after the ordered ones, in their usual order.
CREATE TABLE blueprint_order (
    id       TEXT PRIMARY KEY,
    position INTEGER NOT NULL
) STRICT;
