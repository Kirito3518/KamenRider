CREATE TABLE agent_update_recovery_successors (
    inspection_id TEXT PRIMARY KEY,
    intent_id TEXT NOT NULL UNIQUE REFERENCES agent_update_recovery_intents(intent_id),
    machine_id TEXT NOT NULL REFERENCES machines(machine_id),
    chain_id TEXT NOT NULL,
    previous_head_operation_id TEXT NOT NULL,
    successor_operation_id TEXT NOT NULL UNIQUE REFERENCES agent_update_operations(operation_id),
    generation INTEGER NOT NULL CHECK(generation > 0),
    request_id TEXT NOT NULL CHECK(length(trim(request_id)) BETWEEN 1 AND 128),
    key_fingerprint TEXT NOT NULL CHECK(length(key_fingerprint) = 64 AND key_fingerprint NOT GLOB '*[^0-9A-Fa-f]*'),
    request_body_digest TEXT NOT NULL CHECK(length(request_body_digest) = 64 AND request_body_digest NOT GLOB '*[^0-9A-Fa-f]*'),
    evidence_digest TEXT NOT NULL CHECK(length(evidence_digest) = 64 AND evidence_digest NOT GLOB '*[^0-9A-Fa-f]*'),
    helper_build_sha256 TEXT NOT NULL CHECK(length(helper_build_sha256) = 64 AND helper_build_sha256 NOT GLOB '*[^0-9A-Fa-f]*'),
    helper_runtime TEXT NOT NULL CHECK(helper_runtime IN ('win-x64','win-arm64')),
    helper_protocol INTEGER NOT NULL CHECK(helper_protocol = 1),
    exclusion_provenance TEXT NOT NULL CHECK(exclusion_provenance IN ('continuousSharedLock','verifiedFullHostRestart')),
    old_updater_auto_resume_status TEXT NOT NULL CHECK(old_updater_auto_resume_status IN ('qualifiedNotApplicable','qualifiedDisabled')),
    actual_installed_version TEXT NOT NULL CHECK(length(trim(actual_installed_version)) BETWEEN 1 AND 64),
    actual_installed_digest TEXT NOT NULL CHECK(length(actual_installed_digest) = 64 AND actual_installed_digest NOT GLOB '*[^0-9A-Fa-f]*'),
    native_boot_id_before TEXT NOT NULL CHECK(length(native_boot_id_before) = 32),
    native_boot_id_after TEXT NOT NULL CHECK(length(native_boot_id_after) = 32),
    ticket_hash TEXT NOT NULL UNIQUE CHECK(length(ticket_hash) = 64 AND ticket_hash NOT GLOB '*[^0-9A-Fa-f]*'),
    created_at_unix_ms INTEGER NOT NULL CHECK(created_at_unix_ms >= 0),
    FOREIGN KEY(chain_id,previous_head_operation_id) REFERENCES agent_update_operations(chain_id,operation_id),
    FOREIGN KEY(chain_id,successor_operation_id) REFERENCES agent_update_operations(chain_id,operation_id)
);
CREATE INDEX agent_update_recovery_successor_machine
    ON agent_update_recovery_successors(machine_id,created_at_unix_ms);
CREATE INDEX agent_update_recovery_successor_chain
    ON agent_update_recovery_successors(chain_id,generation);

CREATE TRIGGER agent_update_recovery_successor_identity_immutable
BEFORE UPDATE OF inspection_id,intent_id,machine_id,chain_id,previous_head_operation_id,successor_operation_id,
    generation,request_id,key_fingerprint,request_body_digest,evidence_digest,helper_build_sha256,helper_runtime,
    helper_protocol,exclusion_provenance,old_updater_auto_resume_status,actual_installed_version,
    actual_installed_digest,native_boot_id_before,native_boot_id_after,ticket_hash,created_at_unix_ms
ON agent_update_recovery_successors
BEGIN SELECT RAISE(ABORT,'Agent update recovery successor is immutable'); END;

CREATE TRIGGER agent_update_recovery_successor_no_delete
BEFORE DELETE ON agent_update_recovery_successors
BEGIN SELECT RAISE(ABORT,'Agent update recovery successor history is immutable'); END;

PRAGMA user_version=21;
