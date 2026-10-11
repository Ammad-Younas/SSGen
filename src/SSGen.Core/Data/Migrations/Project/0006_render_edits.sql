-- When an image was last changed in the editor (null = straight from the template). Rendering from the Design
-- page again asks before replacing edited images.
ALTER TABLE renders ADD COLUMN edited_utc TEXT;
