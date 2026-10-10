-- Exploration runs and the screen graph they build.
CREATE TABLE agent_runs (
    id            INTEGER PRIMARY KEY AUTOINCREMENT,
    started_utc   TEXT NOT NULL,
    ended_utc     TEXT,
    strategy      TEXT NOT NULL,
    status        TEXT NOT NULL,          -- running | completed | stopped | failed | limit
    steps         INTEGER NOT NULL DEFAULT 0,
    screens       INTEGER NOT NULL DEFAULT 0,
    input_tokens  INTEGER NOT NULL DEFAULT 0,
    output_tokens INTEGER NOT NULL DEFAULT 0,
    cost_usd      REAL,
    summary       TEXT
);

CREATE TABLE screen_nodes (
    id        INTEGER PRIMARY KEY AUTOINCREMENT,
    run_id    INTEGER NOT NULL REFERENCES agent_runs(id) ON DELETE CASCADE,
    node_key  INTEGER NOT NULL,            -- id within the run's graph
    screen_id INTEGER REFERENCES screens(id) ON DELETE SET NULL,
    activity  TEXT,
    dhash     TEXT NOT NULL,
    label     TEXT,
    visits    INTEGER NOT NULL DEFAULT 1,
    UNIQUE(run_id, node_key)
);

CREATE TABLE screen_edges (
    id        INTEGER PRIMARY KEY AUTOINCREMENT,
    run_id    INTEGER NOT NULL REFERENCES agent_runs(id) ON DELETE CASCADE,
    from_node INTEGER NOT NULL,
    to_node   INTEGER NOT NULL,
    action    TEXT NOT NULL
);

CREATE TABLE agent_steps (
    id      INTEGER PRIMARY KEY AUTOINCREMENT,
    run_id  INTEGER NOT NULL REFERENCES agent_runs(id) ON DELETE CASCADE,
    step    INTEGER NOT NULL,
    kind    TEXT NOT NULL,                 -- thought | action | blocked | screen | warning | error
    message TEXT NOT NULL,
    ts_utc  TEXT NOT NULL
);
