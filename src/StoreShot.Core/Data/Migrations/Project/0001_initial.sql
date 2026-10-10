-- Per-project database, stored as project.db inside the project folder.
CREATE TABLE project_meta (
    key   TEXT PRIMARY KEY NOT NULL,
    value TEXT NOT NULL
);
