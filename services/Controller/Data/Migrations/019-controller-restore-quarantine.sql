CREATE TABLE controller_restore_state (
    singleton INTEGER PRIMARY KEY CHECK(singleton=1),
    quarantine_active INTEGER NOT NULL CHECK(quarantine_active IN (0,1)),
    quarantine_reason TEXT NOT NULL CHECK(length(trim(quarantine_reason)) BETWEEN 1 AND 128),
    quarantine_epoch INTEGER NOT NULL CHECK(quarantine_epoch>=0),
    database_epoch INTEGER NOT NULL DEFAULT 0 CHECK(database_epoch>=0),
    quarantined_at_unix_ms INTEGER NOT NULL CHECK(quarantined_at_unix_ms>=0),
    updated_at_unix_ms INTEGER NOT NULL CHECK(updated_at_unix_ms>=0)
);
INSERT INTO controller_restore_state(singleton,quarantine_active,quarantine_reason,quarantine_epoch,quarantined_at_unix_ms,updated_at_unix_ms)
VALUES(1,0,'initial',0,0,0);

PRAGMA user_version=19;
