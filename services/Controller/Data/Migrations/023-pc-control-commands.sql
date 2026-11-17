-- PC-control admin commands.
--
-- The command outbox gains two explicit machine/lease control commands:
--   * ShutdownMachine: machine-scoped, no rental (forced power off)
--   * EndLease: lease-scoped, closes the stream gate and clears the local lease
--
-- The table is rebuilt because SQLite cannot alter the original CHECK constraint.
CREATE TEMP TABLE command_outbox_migration_backup AS
    SELECT rowid AS migration_rowid, * FROM main.command_outbox;
DROP TABLE main.command_outbox;
CREATE TABLE main.command_outbox (
    command_id TEXT PRIMARY KEY,
    type TEXT NOT NULL CHECK(type IN ('PrepareMachine','ApplyLease','PairPin','EndLease','ShutdownMachine')),
    rental_id TEXT REFERENCES leases(id),
    machine_id TEXT NOT NULL REFERENCES machines(machine_id),
    lease_revision INTEGER NOT NULL DEFAULT 0,
    boot_id TEXT NOT NULL,
    valid_until INTEGER NOT NULL,
    payload TEXT NOT NULL,
    status TEXT NOT NULL DEFAULT 'pending' CHECK(status IN ('pending','applied','failed','expired','invalidated')),
    error_code TEXT,
    CHECK ((type='ShutdownMachine' AND rental_id IS NULL AND lease_revision=0)
        OR (type<>'ShutdownMachine' AND rental_id IS NOT NULL AND lease_revision>0))
);
INSERT INTO main.command_outbox(rowid,command_id,type,rental_id,machine_id,lease_revision,boot_id,valid_until,payload,status,error_code)
    SELECT migration_rowid,command_id,type,rental_id,machine_id,lease_revision,boot_id,valid_until,payload,status,error_code
    FROM temp.command_outbox_migration_backup ORDER BY migration_rowid;
DROP TABLE temp.command_outbox_migration_backup;
CREATE UNIQUE INDEX lease_command_revision ON command_outbox(rental_id,type,lease_revision)
    WHERE type NOT IN ('PairPin','ShutdownMachine');
CREATE UNIQUE INDEX machine_shutdown_pending ON command_outbox(machine_id)
    WHERE type='ShutdownMachine' AND status='pending';

-- Administrative actions are append-only audit/idempotency records. They deliberately never
-- contain pairing PINs or other secrets.
CREATE TABLE machine_admin_actions (
    action_id TEXT PRIMARY KEY,
    machine_id TEXT NOT NULL REFERENCES machines(machine_id),
    actor_id TEXT NOT NULL,
    request_id TEXT NOT NULL,
    action_type TEXT NOT NULL CHECK(action_type IN ('powerOff','cancelLease','adjustTime','pairPin')),
    command_id TEXT,
    payload_digest TEXT NOT NULL CHECK(length(payload_digest)=64 AND payload_digest NOT GLOB '*[^0-9A-Fa-f]*'),
    created_at_unix_ms INTEGER NOT NULL CHECK(created_at_unix_ms >= 0),
    UNIQUE(actor_id,request_id)
);
CREATE INDEX machine_admin_action_machine
    ON machine_admin_actions(machine_id,created_at_unix_ms);
CREATE TRIGGER machine_admin_action_immutable_update
BEFORE UPDATE ON machine_admin_actions
BEGIN SELECT RAISE(ABORT,'Machine admin action is immutable'); END;
CREATE TRIGGER machine_admin_action_immutable_delete
BEFORE DELETE ON machine_admin_actions
BEGIN SELECT RAISE(ABORT,'Machine admin action is immutable'); END;

PRAGMA user_version=23;
