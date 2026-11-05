CREATE TABLE IF NOT EXISTS agent_enrollment_tickets(
 token_hash TEXT PRIMARY KEY,
 expires_at_unix_ms INTEGER NOT NULL
);
CREATE INDEX IF NOT EXISTS agent_enrollment_tickets_expiry ON agent_enrollment_tickets(expires_at_unix_ms);
