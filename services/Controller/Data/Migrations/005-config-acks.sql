CREATE TABLE IF NOT EXISTS machine_config_acks(machine_id TEXT PRIMARY KEY REFERENCES machines(machine_id) ON DELETE CASCADE,applied_revision INTEGER NOT NULL);
