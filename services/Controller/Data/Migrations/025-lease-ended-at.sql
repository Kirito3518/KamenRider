-- Effective end instant of a lease, recorded once when the lease leaves the active window.
ALTER TABLE leases ADD COLUMN ended_at INTEGER;
PRAGMA user_version=25;
