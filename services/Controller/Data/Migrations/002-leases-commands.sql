CREATE TABLE IF NOT EXISTS leases (
 id TEXT PRIMARY KEY, user_id TEXT NOT NULL, machine_id TEXT NOT NULL REFERENCES machines(machine_id),
 state TEXT NOT NULL CHECK(state IN ('preparing','active','failed','ending')),
 revision INTEGER NOT NULL DEFAULT 0, pending_revision INTEGER, duration_seconds INTEGER NOT NULL,
 preparation_deadline INTEGER NOT NULL, starts_at INTEGER, expires_at INTEGER, pending_expires_at INTEGER,
 boot_id TEXT, allocation_held INTEGER NOT NULL DEFAULT 1 CHECK(allocation_held IN (0,1))
);
CREATE UNIQUE INDEX IF NOT EXISTS leases_machine_allocation ON leases(machine_id) WHERE allocation_held=1;
CREATE INDEX IF NOT EXISTS leases_user ON leases(user_id);
CREATE TABLE IF NOT EXISTS lease_requests (
 scope TEXT NOT NULL, request_key TEXT NOT NULL, fingerprint TEXT NOT NULL,
 rental_id TEXT NOT NULL REFERENCES leases(id), PRIMARY KEY(scope,request_key)
);
CREATE TABLE IF NOT EXISTS command_outbox (
 command_id TEXT PRIMARY KEY, type TEXT NOT NULL CHECK(type IN ('PrepareMachine','ApplyLease','PairPin')),
 rental_id TEXT NOT NULL REFERENCES leases(id), machine_id TEXT NOT NULL REFERENCES machines(machine_id),
 lease_revision INTEGER NOT NULL, boot_id TEXT NOT NULL, valid_until INTEGER NOT NULL, payload TEXT NOT NULL,
 status TEXT NOT NULL DEFAULT 'pending' CHECK(status IN ('pending','applied','failed','expired','invalidated')),
 error_code TEXT, UNIQUE(rental_id,type,lease_revision)
);
CREATE TABLE IF NOT EXISTS machine_boots (
 machine_id TEXT NOT NULL REFERENCES machines(machine_id), boot_id TEXT NOT NULL, retired INTEGER NOT NULL DEFAULT 0,
 PRIMARY KEY(machine_id,boot_id)
);
