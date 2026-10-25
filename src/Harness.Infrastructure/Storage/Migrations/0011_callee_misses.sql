-- Callee misses (HAR-0145 evidence): a missed file whose changed function
-- is called by code get_context returned. called_from names that returned
-- file; NULL for other misses and for hits.
ALTER TABLE context_outcomes ADD COLUMN called_from TEXT;
