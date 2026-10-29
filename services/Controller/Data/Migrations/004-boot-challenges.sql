CREATE TABLE IF NOT EXISTS boot_reconciliation_challenges (
 machine_id TEXT PRIMARY KEY REFERENCES machines(machine_id), token TEXT NOT NULL,
 boot_id TEXT NOT NULL, allocation_id TEXT, lease_revision INTEGER,
 fingerprint TEXT NOT NULL, expires_at INTEGER NOT NULL
);
PRAGMA user_version=4;
