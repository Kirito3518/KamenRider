CREATE TEMP TABLE leases_migration_backup AS SELECT rowid AS migration_rowid, * FROM main.leases;
CREATE TEMP TABLE lease_requests_migration_backup AS SELECT rowid AS migration_rowid, * FROM main.lease_requests;
CREATE TEMP TABLE command_outbox_migration_backup AS SELECT rowid AS migration_rowid, * FROM main.command_outbox;
CREATE TEMP TABLE pairing_requests_migration_backup AS SELECT rowid AS migration_rowid, * FROM main.pairing_requests;

DROP TABLE main.pairing_requests;
DROP TABLE main.command_outbox;
DROP TABLE main.lease_requests;
DROP TABLE main.leases;

CREATE TABLE main.leases (
 id TEXT PRIMARY KEY, user_id TEXT NOT NULL, machine_id TEXT NOT NULL REFERENCES machines(machine_id),
 state TEXT NOT NULL CHECK(state IN ('preparing','active','failed','ending','completed')),
 revision INTEGER NOT NULL DEFAULT 0, pending_revision INTEGER, duration_seconds INTEGER NOT NULL,
 preparation_deadline INTEGER NOT NULL, starts_at INTEGER, expires_at INTEGER, pending_expires_at INTEGER,
 boot_id TEXT, allocation_held INTEGER NOT NULL DEFAULT 1 CHECK(allocation_held IN (0,1))
);
INSERT INTO main.leases(rowid,id,user_id,machine_id,state,revision,pending_revision,duration_seconds,preparation_deadline,starts_at,expires_at,pending_expires_at,boot_id,allocation_held)
 SELECT migration_rowid,id,user_id,machine_id,state,revision,pending_revision,duration_seconds,preparation_deadline,starts_at,expires_at,pending_expires_at,boot_id,allocation_held
 FROM temp.leases_migration_backup ORDER BY migration_rowid;
CREATE UNIQUE INDEX leases_machine_allocation ON leases(machine_id) WHERE allocation_held=1;
CREATE INDEX leases_user ON leases(user_id);

CREATE TABLE main.lease_requests (
 scope TEXT NOT NULL, request_key TEXT NOT NULL, fingerprint TEXT NOT NULL,
 rental_id TEXT NOT NULL REFERENCES leases(id), PRIMARY KEY(scope,request_key)
);
INSERT INTO main.lease_requests(rowid,scope,request_key,fingerprint,rental_id)
 SELECT migration_rowid,scope,request_key,fingerprint,rental_id FROM temp.lease_requests_migration_backup ORDER BY migration_rowid;

CREATE TABLE main.command_outbox (
 command_id TEXT PRIMARY KEY, type TEXT NOT NULL CHECK(type IN ('PrepareMachine','ApplyLease','PairPin')),
 rental_id TEXT NOT NULL REFERENCES leases(id), machine_id TEXT NOT NULL REFERENCES machines(machine_id),
 lease_revision INTEGER NOT NULL, boot_id TEXT NOT NULL, valid_until INTEGER NOT NULL, payload TEXT NOT NULL,
 status TEXT NOT NULL DEFAULT 'pending' CHECK(status IN ('pending','applied','failed','expired','invalidated')),
 error_code TEXT
);
INSERT INTO main.command_outbox(rowid,command_id,type,rental_id,machine_id,lease_revision,boot_id,valid_until,payload,status,error_code)
 SELECT migration_rowid,command_id,type,rental_id,machine_id,lease_revision,boot_id,valid_until,payload,status,error_code FROM temp.command_outbox_migration_backup ORDER BY migration_rowid;
CREATE UNIQUE INDEX lease_command_revision ON command_outbox(rental_id,type,lease_revision) WHERE type!='PairPin';

CREATE TABLE main.pairing_requests (
 rental_id TEXT NOT NULL, request_key TEXT NOT NULL, pairing_id TEXT NOT NULL, command_id TEXT NOT NULL,
 PRIMARY KEY(rental_id,request_key)
);
INSERT INTO main.pairing_requests(rowid,rental_id,request_key,pairing_id,command_id)
 SELECT migration_rowid,rental_id,request_key,pairing_id,command_id FROM temp.pairing_requests_migration_backup ORDER BY migration_rowid;

DROP TABLE temp.pairing_requests_migration_backup;
DROP TABLE temp.command_outbox_migration_backup;
DROP TABLE temp.lease_requests_migration_backup;
DROP TABLE temp.leases_migration_backup;
PRAGMA user_version=10;
