-- Captured screens (manual captures now; the exploration agent adds its own from M3).
CREATE TABLE screens (
    id           INTEGER PRIMARY KEY AUTOINCREMENT,
    file         TEXT NOT NULL,              -- path relative to the project folder
    source       TEXT NOT NULL,              -- 'manual' | 'agent'
    activity     TEXT,                       -- foreground activity when captured, if known
    width        INTEGER NOT NULL,
    height       INTEGER NOT NULL,
    captured_utc TEXT NOT NULL
);
