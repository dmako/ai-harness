-- Milestones are now blueprints (TASK-0047): the table, the tasks' column
-- (its index follows the rename) and the key in proposed task definitions.
ALTER TABLE milestones RENAME TO blueprints;
ALTER TABLE tasks RENAME COLUMN milestone TO blueprint;
UPDATE task_proposals
SET definition = json_set(json_remove(definition, '$.milestone'), '$.blueprint', json_extract(definition, '$.milestone'))
WHERE json_type(definition, '$.milestone') IS NOT NULL;
