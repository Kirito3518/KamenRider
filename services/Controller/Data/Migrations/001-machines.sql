CREATE TABLE IF NOT EXISTS machines (
 machine_id TEXT PRIMARY KEY, public_key TEXT NOT NULL, approval_status TEXT NOT NULL DEFAULT 'pending',
 display_name TEXT NOT NULL DEFAULT '', hourly_price INTEGER NOT NULL DEFAULT 0,
 local_ip TEXT NOT NULL, mac_address TEXT NOT NULL, moonlight_port INTEGER NOT NULL CHECK(moonlight_port BETWEEN 1 AND 65535),
 sunshine_port INTEGER NOT NULL CHECK(sunshine_port BETWEEN 1 AND 65535), revision INTEGER NOT NULL DEFAULT 1,
 approved_by TEXT, approved_at TEXT
);
CREATE TABLE IF NOT EXISTS machine_approval_audit (id INTEGER PRIMARY KEY, machine_id TEXT NOT NULL REFERENCES machines(machine_id), actor_id TEXT NOT NULL, approved_at TEXT NOT NULL);
