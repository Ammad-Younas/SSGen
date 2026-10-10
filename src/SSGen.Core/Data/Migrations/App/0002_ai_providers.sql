-- AI provider profiles. API keys are NOT here: they live DPAPI-encrypted in `secrets` as "provider:<id>".
CREATE TABLE provider_profiles (
    id              TEXT PRIMARY KEY NOT NULL,
    name            TEXT NOT NULL,
    type            TEXT NOT NULL,              -- Anthropic | OpenAI | OpenAICompatible | Gemini | Ollama
    base_url        TEXT,
    model           TEXT NOT NULL,
    vision_mode     TEXT NOT NULL DEFAULT 'Auto',
    detected_vision INTEGER,                    -- cached detection result (NULL = unknown)
    created_utc     TEXT NOT NULL
);

-- User-editable prices (USD per million tokens). No defaults ship: prices change, so the user enters them.
CREATE TABLE model_prices (
    model           TEXT PRIMARY KEY NOT NULL COLLATE NOCASE,
    input_per_mtok  REAL NOT NULL,
    output_per_mtok REAL NOT NULL,
    updated_utc     TEXT NOT NULL
);

-- Which profile/model handles each AI task (exploration, ranking, headlines, translation). Missing = active profile.
CREATE TABLE task_models (
    task       TEXT PRIMARY KEY NOT NULL,
    profile_id TEXT REFERENCES provider_profiles(id) ON DELETE SET NULL,
    model      TEXT
);
