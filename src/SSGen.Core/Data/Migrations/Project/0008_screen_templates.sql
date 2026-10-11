-- A design (template id) for one screen in the set, overriding the set's template. NULL = use the set's.
-- Kept apart from the ranking columns so ranking again never resets it.
ALTER TABLE selection ADD COLUMN template_id TEXT;
