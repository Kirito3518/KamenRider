CREATE TABLE agent_releases (
    version TEXT PRIMARY KEY,
    channel TEXT NOT NULL CHECK(channel IN ('stable','beta')),
    release_notes TEXT NOT NULL,
    package_sha256 TEXT NOT NULL,
    package_path TEXT NOT NULL,
    package_size_bytes INTEGER NOT NULL CHECK(package_size_bytes > 0),
    source_commit TEXT NOT NULL,
    source_dirty INTEGER NOT NULL CHECK(source_dirty IN (0,1)),
    release_build INTEGER NOT NULL CHECK(release_build IN (0,1)),
    package_format TEXT NOT NULL CHECK(package_format='AgentPackage.zip'),
    published_at_unix_ms INTEGER NOT NULL
);

CREATE TRIGGER agent_releases_immutable_update
BEFORE UPDATE ON agent_releases
BEGIN
    SELECT RAISE(ABORT, 'Agent releases are immutable');
END;

CREATE TRIGGER agent_releases_immutable_delete
BEFORE DELETE ON agent_releases
BEGIN
    SELECT RAISE(ABORT, 'Agent releases are immutable');
END;

CREATE TABLE agent_update_operations (
    operation_id TEXT PRIMARY KEY,
    machine_id TEXT NOT NULL REFERENCES machines(machine_id),
    release_version TEXT NOT NULL REFERENCES agent_releases(version),
    target_version TEXT NOT NULL,
    target_package_sha256 TEXT NOT NULL,
    previous_version TEXT NOT NULL,
    status TEXT NOT NULL CHECK(status IN ('awaitingInstaller','installing','awaitingHeartbeat','rollingBack','completed','rolledBack','expired','failed')),
    ticket_hash TEXT NOT NULL UNIQUE,
    ticket_expires_at_unix_ms INTEGER NOT NULL,
    ticket_started_at_unix_ms INTEGER,
    started_at_unix_ms INTEGER,
    created_at_unix_ms INTEGER NOT NULL,
    updated_at_unix_ms INTEGER NOT NULL
);

CREATE UNIQUE INDEX agent_update_one_active_per_machine
    ON agent_update_operations(machine_id)
    WHERE status NOT IN ('completed','rolledBack','expired');
CREATE INDEX agent_update_unstarted_expiry
    ON agent_update_operations(status, ticket_expires_at_unix_ms, started_at_unix_ms);

PRAGMA user_version=12;
