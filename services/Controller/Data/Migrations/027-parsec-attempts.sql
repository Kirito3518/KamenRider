-- Parsec renter-account integration.
--
-- The Agent advertises a boot-only RSA public key in its signed heartbeat. The browser seals
-- the renter's Parsec credentials with that key; the Controller only stores/relays ciphertext in
-- a lease-scoped ParsecConnect command until the Agent accepts it, then the ciphertext is purged.
--
-- The command_outbox is rebuilt again because SQLite cannot alter its CHECK constraint.
CREATE TEMP TABLE command_outbox_migration_backup AS
    SELECT rowid AS migration_rowid, * FROM main.command_outbox;
DROP TABLE main.command_outbox;
CREATE TABLE main.command_outbox (
    command_id TEXT PRIMARY KEY,
    type TEXT NOT NULL CHECK(type IN ('PrepareMachine','ApplyLease','PairPin','EndLease','ShutdownMachine','RestartSunshine','UnpairSunshine','ParsecConnect')),
    rental_id TEXT REFERENCES leases(id),
    machine_id TEXT NOT NULL REFERENCES machines(machine_id),
    lease_revision INTEGER NOT NULL DEFAULT 0,
    boot_id TEXT NOT NULL,
    valid_until INTEGER NOT NULL,
    payload TEXT NOT NULL,
    status TEXT NOT NULL DEFAULT 'pending' CHECK(status IN ('pending','applied','failed','expired','invalidated')),
    error_code TEXT,
    CHECK ((type IN ('ShutdownMachine','RestartSunshine','UnpairSunshine') AND rental_id IS NULL AND lease_revision=0)
        OR (type NOT IN ('ShutdownMachine','RestartSunshine','UnpairSunshine') AND rental_id IS NOT NULL AND lease_revision>0))
);
INSERT INTO main.command_outbox(rowid,command_id,type,rental_id,machine_id,lease_revision,boot_id,valid_until,payload,status,error_code)
    SELECT migration_rowid,command_id,type,rental_id,machine_id,lease_revision,boot_id,valid_until,payload,status,error_code
    FROM temp.command_outbox_migration_backup ORDER BY migration_rowid;
DROP TABLE temp.command_outbox_migration_backup;
CREATE UNIQUE INDEX lease_command_revision ON command_outbox(rental_id,type,lease_revision)
    WHERE type NOT IN ('PairPin','ShutdownMachine','RestartSunshine','UnpairSunshine','ParsecConnect');
CREATE UNIQUE INDEX machine_shutdown_pending ON command_outbox(machine_id)
    WHERE type='ShutdownMachine' AND status='pending';
CREATE UNIQUE INDEX machine_sunshine_pending ON command_outbox(machine_id)
    WHERE type IN ('RestartSunshine','UnpairSunshine') AND status='pending';
CREATE UNIQUE INDEX lease_parsec_pending ON command_outbox(rental_id)
    WHERE type='ParsecConnect' AND status='pending';

-- Admin opt-in per machine; the Agent still has to advertise the capability (parsecd.exe found).
ALTER TABLE machines ADD COLUMN parsec_enabled INTEGER NOT NULL DEFAULT 0 CHECK(parsec_enabled IN (0,1));

-- Last capability advertised by the Agent. The public key is ephemeral (one boot) and is only
-- exposed to the owner of an active lease.
CREATE TABLE machine_parsec_state (
    machine_id TEXT PRIMARY KEY REFERENCES machines(machine_id),
    available INTEGER NOT NULL CHECK(available IN (0,1)),
    boot_key_id TEXT,
    public_key TEXT,
    updated_at_unix_ms INTEGER NOT NULL CHECK(updated_at_unix_ms >= 0),
    CHECK (available=0 OR (boot_key_id IS NOT NULL AND public_key IS NOT NULL))
);

CREATE TABLE parsec_attempts (
    attempt_id TEXT PRIMARY KEY,
    machine_id TEXT NOT NULL REFERENCES machines(machine_id),
    rental_id TEXT NOT NULL REFERENCES leases(id),
    user_id TEXT NOT NULL,
    idempotency_key TEXT NOT NULL UNIQUE,
    state TEXT NOT NULL CHECK(state IN ('queued','starting','waiting_email','ready','failed')),
    error_code TEXT,
    message TEXT,
    boot_key_id TEXT,
    created_at_unix_ms INTEGER NOT NULL CHECK(created_at_unix_ms >= 0),
    updated_at_unix_ms INTEGER NOT NULL CHECK(updated_at_unix_ms >= 0),
    expires_at_unix_ms INTEGER NOT NULL CHECK(expires_at_unix_ms >= 0)
);
CREATE INDEX parsec_attempt_rental ON parsec_attempts(rental_id,created_at_unix_ms);
CREATE INDEX parsec_attempt_machine ON parsec_attempts(machine_id,created_at_unix_ms);

PRAGMA user_version=27;
