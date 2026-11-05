CREATE TABLE IF NOT EXISTS admin_vnc_sessions(
 session_id TEXT NOT NULL UNIQUE,
 ticket_hash TEXT PRIMARY KEY,
 machine_id TEXT NOT NULL REFERENCES machines(machine_id),
 admin_user_id TEXT NOT NULL,
 allowed_origin TEXT NOT NULL,
 issued_at_unix_ms INTEGER NOT NULL,
 expires_at_unix_ms INTEGER NOT NULL,
 used_at_unix_ms INTEGER,
 ended_at_unix_ms INTEGER
);
CREATE INDEX IF NOT EXISTS admin_vnc_sessions_expiry ON admin_vnc_sessions(expires_at_unix_ms);
