-- Dependencies on tasks of other projects (TASK-0059, as the Go harness's
-- TASK-0058): written <project>:<task id>, the project by a name the machine
-- registry knows. The other task's status is read from its project's database
-- (read-only) and kept here with when it was read, so readiness stays one query
-- of this database.
CREATE TABLE task_external_dependencies (
    task_id    TEXT NOT NULL REFERENCES tasks (id),
    project    TEXT NOT NULL,
    depends_on TEXT NOT NULL,
    status     TEXT NOT NULL DEFAULT '',  -- the other task's status when read ('' not yet)
    checked_at TEXT,                      -- when it was read
    PRIMARY KEY (task_id, project, depends_on)
) STRICT;
