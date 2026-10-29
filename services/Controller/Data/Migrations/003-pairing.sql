CREATE TABLE IF NOT EXISTS pairing_offers(machine_id TEXT PRIMARY KEY,rental_id TEXT NOT NULL,revision INTEGER NOT NULL,boot_id TEXT NOT NULL,valid_until INTEGER NOT NULL,ids TEXT NOT NULL);
CREATE TABLE IF NOT EXISTS pairing_requests(rental_id TEXT NOT NULL,request_key TEXT NOT NULL,pairing_id TEXT NOT NULL,command_id TEXT NOT NULL,PRIMARY KEY(rental_id,request_key));
ALTER TABLE command_outbox RENAME TO command_outbox_before_pairing;
CREATE TABLE command_outbox (
 command_id TEXT PRIMARY KEY,type TEXT NOT NULL CHECK(type IN ('PrepareMachine','ApplyLease','PairPin')),
 rental_id TEXT NOT NULL REFERENCES leases(id),machine_id TEXT NOT NULL REFERENCES machines(machine_id),
 lease_revision INTEGER NOT NULL,boot_id TEXT NOT NULL,valid_until INTEGER NOT NULL,payload TEXT NOT NULL,
 status TEXT NOT NULL DEFAULT 'pending' CHECK(status IN ('pending','applied','failed','expired','invalidated')),error_code TEXT
);
INSERT INTO command_outbox SELECT * FROM command_outbox_before_pairing;
DROP TABLE command_outbox_before_pairing;
CREATE UNIQUE INDEX lease_command_revision ON command_outbox(rental_id,type,lease_revision) WHERE type!='PairPin';
PRAGMA user_version=3;
