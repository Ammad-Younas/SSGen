-- Captions in additional languages (the primary language stays in the selection table) and the language of
-- each rendered image ('' = the primary language).
CREATE TABLE captions (
    screen_id    INTEGER NOT NULL REFERENCES screens(id) ON DELETE CASCADE,
    locale       TEXT NOT NULL,          -- BCP 47 tag, e.g. ur, ar, de-DE
    headline     TEXT NOT NULL DEFAULT '',
    subheadline  TEXT NOT NULL DEFAULT '',
    source       TEXT NOT NULL,          -- manual | ai
    updated_utc  TEXT NOT NULL,
    PRIMARY KEY (screen_id, locale)
);

ALTER TABLE renders ADD COLUMN locale TEXT NOT NULL DEFAULT '';
