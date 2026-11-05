-- Each blueprint has a colour (TASK-0081), one of the 16 of BlueprintColors.Names,
-- shown on harness ui's Board and in Work › Tasks. Existing blueprints get them in
-- the backlog's order, as given out one by one: the first colour no other uses, the
-- set starting again after 16. The list below is that set, frozen with this migration.
ALTER TABLE blueprints ADD COLUMN color TEXT NOT NULL DEFAULT '';
UPDATE blueprints SET color = (
    SELECT CASE (n - 1) % 16
        WHEN 0 THEN 'teal' WHEN 1 THEN 'orange' WHEN 2 THEN 'blue' WHEN 3 THEN 'rose'
        WHEN 4 THEN 'green' WHEN 5 THEN 'violet' WHEN 6 THEN 'amber' WHEN 7 THEN 'cyan'
        WHEN 8 THEN 'pink' WHEN 9 THEN 'lime' WHEN 10 THEN 'indigo' WHEN 11 THEN 'red'
        WHEN 12 THEN 'fuchsia' WHEN 13 THEN 'yellow' WHEN 14 THEN 'slate' ELSE 'purple' END
    FROM (SELECT id AS bid, ROW_NUMBER() OVER (ORDER BY ordinal, id) AS n FROM blueprints)
    WHERE bid = blueprints.id
);
