-- Global app database (one per Windows user).
CREATE TABLE settings (
    key        TEXT PRIMARY KEY NOT NULL,
    value      TEXT NOT NULL
);

-- Values are DPAPI-encrypted (CurrentUser scope). Never store plaintext here.
CREATE TABLE secrets (
    name        TEXT PRIMARY KEY NOT NULL,
    ciphertext  BLOB NOT NULL,
    updated_utc TEXT NOT NULL
);

CREATE TABLE recent_projects (
    path            TEXT PRIMARY KEY NOT NULL COLLATE NOCASE,
    name            TEXT NOT NULL,
    last_opened_utc TEXT NOT NULL
);
