-- Screen text (for headlines without AI), the chosen set, cached AI vision results, and rendered images.
ALTER TABLE screens ADD COLUMN title TEXT;     -- app bar title from the UI tree, when known
ALTER TABLE screens ADD COLUMN ui_text TEXT;   -- visible text on the screen, newline-separated

CREATE TABLE selection (
    screen_id    INTEGER PRIMARY KEY REFERENCES screens(id) ON DELETE CASCADE,
    included     INTEGER NOT NULL DEFAULT 0,
    position     INTEGER,
    score        REAL,
    reject       TEXT,                   -- why it shouldn't be used (blank, loading, login, duplicate, ...)
    label        TEXT,
    headline     TEXT,
    subheadline  TEXT,
    reasoning    TEXT,
    source       TEXT NOT NULL,          -- heuristic | ai | manual
    updated_utc  TEXT NOT NULL
);

CREATE TABLE vision_cache (
    key          TEXT PRIMARY KEY,       -- hash of image(s) + model + prompt version
    response     TEXT NOT NULL,
    created_utc  TEXT NOT NULL
);

CREATE TABLE renders (
    id           INTEGER PRIMARY KEY AUTOINCREMENT,
    screen_id    INTEGER REFERENCES screens(id) ON DELETE SET NULL,
    preset_id    TEXT NOT NULL,
    template_id  TEXT NOT NULL,
    position     INTEGER NOT NULL,
    file         TEXT NOT NULL,          -- relative to the project folder
    scene_file   TEXT NOT NULL,          -- the editable scene document (JSON)
    width        INTEGER NOT NULL,
    height       INTEGER NOT NULL,
    created_utc  TEXT NOT NULL
);
