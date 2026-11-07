CREATE TABLE machine_readiness_diagnostics (
    machine_id TEXT PRIMARY KEY REFERENCES machines(machine_id) ON DELETE CASCADE,
    diagnostics_json TEXT NOT NULL,
    updated_at TEXT NOT NULL
);
PRAGMA user_version=11;
