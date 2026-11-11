CREATE TABLE agent_sync_proof_state (
    machine_id TEXT PRIMARY KEY REFERENCES machines(machine_id) ON DELETE CASCADE,
    key_fingerprint TEXT,
    mode TEXT NOT NULL DEFAULT 'legacyUnproven' CHECK(mode IN ('legacyUnproven','challengeRequired')),
    sequence INTEGER NOT NULL DEFAULT 0 CHECK(sequence>=0),
    token_sequence INTEGER,
    token_digest TEXT,
    token_epoch TEXT,
    token_issued_tick INTEGER,
    last_proven_sequence INTEGER,
    last_proven_runtime_epoch TEXT,
    last_proven_tick INTEGER,
    last_proven_version TEXT,
    legacy_last_second INTEGER,
    last_received_at TEXT,
    legacy_reported_version TEXT,
    CHECK((token_sequence IS NULL)=(token_digest IS NULL)),
    CHECK((token_sequence IS NULL)=(token_epoch IS NULL)),
    CHECK((token_sequence IS NULL)=(token_issued_tick IS NULL)),
    CHECK(last_proven_runtime_epoch IS NULL OR last_proven_sequence IS NOT NULL),
    CHECK(last_proven_tick IS NULL OR last_proven_runtime_epoch IS NOT NULL)
);
INSERT INTO agent_sync_proof_state(machine_id,last_received_at,legacy_reported_version)
SELECT m.machine_id,h.last_seen,h.agent_version FROM machines m LEFT JOIN machine_heartbeats h ON h.machine_id=m.machine_id;
UPDATE machine_heartbeats SET agent_version=NULL;

CREATE TABLE agent_signed_request_scopes (
    machine_id TEXT NOT NULL,
    key_fingerprint TEXT NOT NULL,
    path TEXT NOT NULL,
    cutoff_second INTEGER NOT NULL,
    PRIMARY KEY(machine_id,key_fingerprint,path)
);
CREATE TABLE agent_signed_request_nonces (
    machine_id TEXT NOT NULL,
    key_fingerprint TEXT NOT NULL,
    path TEXT NOT NULL,
    nonce_digest TEXT NOT NULL,
    signed_second INTEGER NOT NULL,
    PRIMARY KEY(machine_id,key_fingerprint,path,nonce_digest),
    FOREIGN KEY(machine_id,key_fingerprint,path) REFERENCES agent_signed_request_scopes(machine_id,key_fingerprint,path)
);
CREATE INDEX agent_signed_request_nonce_pruning ON agent_signed_request_nonces(machine_id,key_fingerprint,path,signed_second);

CREATE TABLE agent_update_evidence_challenges (
    challenge_id TEXT PRIMARY KEY,
    machine_id TEXT NOT NULL REFERENCES machines(machine_id),
    key_fingerprint TEXT NOT NULL,
    chain_id TEXT NOT NULL,
    head_operation_id TEXT NOT NULL,
    generation INTEGER NOT NULL CHECK(generation>0),
    purpose TEXT NOT NULL CHECK(purpose IN ('source','recoveryInspection')),
    nonce_digest TEXT NOT NULL,
    expires_at_unix_ms INTEGER NOT NULL,
    consumed_at_unix_ms INTEGER,
    request_id TEXT,
    CHECK((consumed_at_unix_ms IS NULL)=(request_id IS NULL)),
    FOREIGN KEY(chain_id,machine_id) REFERENCES agent_maintenance_chains(chain_id,machine_id),
    FOREIGN KEY(chain_id,head_operation_id) REFERENCES agent_update_operations(chain_id,operation_id)
);
CREATE UNIQUE INDEX agent_update_evidence_nonce ON agent_update_evidence_challenges(machine_id,key_fingerprint,purpose,nonce_digest);

CREATE TABLE agent_update_reports (
    operation_id TEXT NOT NULL REFERENCES agent_update_operations(operation_id),
    request_id TEXT NOT NULL,
    body_digest TEXT NOT NULL,
    generation INTEGER NOT NULL CHECK(generation>0),
    status TEXT NOT NULL CHECK(status IN ('awaitingHeartbeat','rollingBack','failed')),
    PRIMARY KEY(operation_id,request_id),
    UNIQUE(operation_id,generation)
);
CREATE TRIGGER agent_update_reports_immutable_update BEFORE UPDATE ON agent_update_reports
BEGIN SELECT RAISE(ABORT,'Agent update reports are append-only'); END;
CREATE TRIGGER agent_update_reports_immutable_delete BEFORE DELETE ON agent_update_reports
BEGIN SELECT RAISE(ABORT,'Agent update reports are append-only'); END;
PRAGMA user_version=15;
