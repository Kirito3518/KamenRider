CREATE TABLE agent_update_recovery_intents (
    intent_id TEXT PRIMARY KEY,
    machine_id TEXT NOT NULL REFERENCES machines(machine_id),
    chain_id TEXT NOT NULL,
    previous_head_operation_id TEXT NOT NULL,
    expected_generation INTEGER NOT NULL CHECK(expected_generation>0),
    generation INTEGER NOT NULL CHECK(generation=expected_generation+1),
    target_release_version TEXT NOT NULL REFERENCES agent_releases(version),
    target_version TEXT NOT NULL,
    target_package_sha256 TEXT NOT NULL CHECK(length(target_package_sha256)=64 AND target_package_sha256 NOT GLOB '*[^0-9A-Fa-f]*'),
    target_runtime TEXT NOT NULL CHECK(target_runtime IN ('win-x64','win-arm64')),
    target_protocol INTEGER NOT NULL CHECK(target_protocol=2),
    reason TEXT NOT NULL CHECK(length(trim(reason)) BETWEEN 1 AND 512),
    actor_id TEXT NOT NULL CHECK(length(trim(actor_id)) BETWEEN 1 AND 128),
    request_id TEXT NOT NULL CHECK(length(trim(request_id)) BETWEEN 1 AND 128),
    request_digest TEXT NOT NULL CHECK(length(request_digest)=64 AND request_digest NOT GLOB '*[^0-9A-Fa-f]*'),
    status TEXT NOT NULL CHECK(status IN ('awaitingInspection','expired','superseded')),
    created_at_unix_ms INTEGER NOT NULL CHECK(created_at_unix_ms>=0),
    expires_at_unix_ms INTEGER NOT NULL CHECK(expires_at_unix_ms>created_at_unix_ms),
    UNIQUE(actor_id,request_id),
    FOREIGN KEY(chain_id,machine_id) REFERENCES agent_maintenance_chains(chain_id,machine_id),
    FOREIGN KEY(chain_id,previous_head_operation_id) REFERENCES agent_update_operations(chain_id,operation_id)
);
CREATE UNIQUE INDEX agent_update_one_open_recovery_intent
    ON agent_update_recovery_intents(chain_id) WHERE status='awaitingInspection';
CREATE INDEX agent_update_recovery_intent_machine
    ON agent_update_recovery_intents(machine_id,status,created_at_unix_ms);

CREATE TABLE agent_update_recovery_inspections (
    inspection_id TEXT PRIMARY KEY,
    intent_id TEXT NOT NULL REFERENCES agent_update_recovery_intents(intent_id),
    request_id TEXT NOT NULL CHECK(length(trim(request_id)) BETWEEN 1 AND 128),
    key_fingerprint TEXT NOT NULL CHECK(length(key_fingerprint)=64 AND key_fingerprint NOT GLOB '*[^0-9A-Fa-f]*'),
    request_body_digest TEXT NOT NULL CHECK(length(request_body_digest)=64 AND request_body_digest NOT GLOB '*[^0-9A-Fa-f]*'),
    evidence_digest TEXT NOT NULL CHECK(length(evidence_digest)=64 AND evidence_digest NOT GLOB '*[^0-9A-Fa-f]*'),
    outcome TEXT NOT NULL CHECK(outcome='blocked'),
    block_reason TEXT NOT NULL CHECK(block_reason IN ('originalBackupBindingUnavailable','helperBuildNotPinned','exclusionProvenanceUnavailable','recoveryChallengeUnavailable')),
    recorded_at_unix_ms INTEGER NOT NULL CHECK(recorded_at_unix_ms>=0),
    UNIQUE(intent_id,request_id)
);
CREATE INDEX agent_update_recovery_inspection_intent
    ON agent_update_recovery_inspections(intent_id,recorded_at_unix_ms);

CREATE TRIGGER agent_update_recovery_intent_identity_immutable
BEFORE UPDATE OF intent_id,machine_id,chain_id,previous_head_operation_id,expected_generation,generation,
    target_release_version,target_version,target_package_sha256,target_runtime,target_protocol,reason,actor_id,
    request_id,request_digest,created_at_unix_ms,expires_at_unix_ms
ON agent_update_recovery_intents
BEGIN SELECT RAISE(ABORT,'Agent update recovery intent identity is immutable'); END;
CREATE TRIGGER agent_update_recovery_intent_status_guard
BEFORE UPDATE OF status ON agent_update_recovery_intents
WHEN NOT (OLD.status='awaitingInspection' AND NEW.status='expired')
 AND NOT (OLD.status='awaitingInspection' AND NEW.status='superseded' AND EXISTS(
     SELECT 1 FROM agent_maintenance_chains c
     JOIN agent_update_operations successor ON successor.operation_id=c.current_head_operation_id
     WHERE c.chain_id=OLD.chain_id AND c.machine_id=OLD.machine_id AND c.held=1
       AND c.current_head_operation_id<>OLD.previous_head_operation_id
       AND successor.parent_operation_id=OLD.previous_head_operation_id
       AND c.generation>OLD.generation))
BEGIN SELECT RAISE(ABORT,'Invalid Agent update recovery intent transition'); END;
CREATE TRIGGER agent_update_recovery_intent_immutable_delete
BEFORE DELETE ON agent_update_recovery_intents
BEGIN SELECT RAISE(ABORT,'Agent update recovery intents are append-only'); END;
CREATE TRIGGER agent_update_recovery_inspection_immutable_update
BEFORE UPDATE ON agent_update_recovery_inspections
BEGIN SELECT RAISE(ABORT,'Agent update recovery inspections are append-only'); END;
CREATE TRIGGER agent_update_recovery_inspection_immutable_delete
BEFORE DELETE ON agent_update_recovery_inspections
BEGIN SELECT RAISE(ABORT,'Agent update recovery inspections are append-only'); END;

PRAGMA user_version=20;
