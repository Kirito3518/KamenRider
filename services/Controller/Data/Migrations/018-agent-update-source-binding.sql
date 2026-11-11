CREATE TABLE agent_update_start_requests (
    operation_id TEXT PRIMARY KEY REFERENCES agent_update_operations(operation_id),
    request_id TEXT NOT NULL,
    body_digest TEXT NOT NULL CHECK(length(body_digest)=64 AND body_digest NOT GLOB '*[^0-9A-Fa-f]*'),
    created_at_unix_ms INTEGER NOT NULL
);
CREATE TRIGGER agent_update_start_request_immutable_update BEFORE UPDATE ON agent_update_start_requests
BEGIN SELECT RAISE(ABORT,'Agent update start request is immutable'); END;
CREATE TRIGGER agent_update_start_request_immutable_delete BEFORE DELETE ON agent_update_start_requests
BEGIN SELECT RAISE(ABORT,'Agent update start request is immutable'); END;

CREATE TABLE agent_update_ticket_runtime_epochs (
    operation_id TEXT PRIMARY KEY REFERENCES agent_update_operations(operation_id),
    controller_runtime_epoch TEXT NOT NULL,
    issued_monotonic_tick INTEGER NOT NULL CHECK(issued_monotonic_tick>=0)
);
CREATE TRIGGER agent_update_ticket_runtime_epoch_immutable_update BEFORE UPDATE ON agent_update_ticket_runtime_epochs
BEGIN SELECT RAISE(ABORT,'Agent update ticket runtime epoch is immutable'); END;
CREATE TRIGGER agent_update_ticket_runtime_epoch_immutable_delete BEFORE DELETE ON agent_update_ticket_runtime_epochs
BEGIN SELECT RAISE(ABORT,'Agent update ticket runtime epoch is immutable'); END;

ALTER TABLE agent_update_evidence_challenges ADD COLUMN challenge_binding_digest TEXT
    CHECK(challenge_binding_digest IS NULL OR (length(challenge_binding_digest)=64 AND challenge_binding_digest NOT GLOB '*[^0-9A-Fa-f]*'));
ALTER TABLE agent_update_evidence_challenges ADD COLUMN challenge_request_id TEXT;
ALTER TABLE agent_update_evidence_challenges ADD COLUMN challenge_issued_at_unix_ms INTEGER;
ALTER TABLE agent_update_evidence_challenges ADD COLUMN challenge_issued_monotonic_tick INTEGER;
ALTER TABLE agent_update_evidence_challenges ADD COLUMN challenge_runtime_epoch TEXT;
CREATE UNIQUE INDEX agent_update_evidence_challenge_binding
    ON agent_update_evidence_challenges(machine_id,key_fingerprint,chain_id,head_operation_id,generation,purpose,challenge_binding_digest)
    WHERE challenge_binding_digest IS NOT NULL;
CREATE UNIQUE INDEX agent_update_evidence_challenge_request
    ON agent_update_evidence_challenges(machine_id,key_fingerprint,chain_id,head_operation_id,generation,purpose,challenge_request_id)
    WHERE challenge_request_id IS NOT NULL;

PRAGMA user_version=18;
