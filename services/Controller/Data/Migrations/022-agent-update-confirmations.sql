-- Relax the maintenance close guard so a chain whose head reached a verified terminal
-- outcome (completed or rolled back) can be released. Starting/inherited maintenance
-- remains held, and any non-terminal head still cannot close the chain.
DROP TRIGGER agent_maintenance_close_guard;
CREATE TRIGGER agent_maintenance_close_guard
BEFORE UPDATE OF held ON agent_maintenance_chains
WHEN (OLD.held=0 AND NEW.held=1)
  OR (OLD.held=1 AND NEW.held=0 AND (
      OLD.inherited=1
      OR NOT EXISTS(SELECT 1 FROM agent_update_operations o
          WHERE o.operation_id=OLD.current_head_operation_id
            AND o.status IN ('completed','rolledBack','expired'))))
BEGIN SELECT RAISE(ABORT,'Started or inherited maintenance remains held'); END;

CREATE TABLE agent_update_confirmations (
    confirmation_id TEXT PRIMARY KEY,
    machine_id TEXT NOT NULL REFERENCES machines(machine_id),
    chain_id TEXT NOT NULL,
    operation_id TEXT NOT NULL,
    generation INTEGER NOT NULL CHECK(generation > 0),
    outcome TEXT NOT NULL CHECK(outcome IN ('completed','rolledBack')),
    proven_version TEXT NOT NULL CHECK(length(trim(proven_version)) BETWEEN 1 AND 64),
    proof_sequence INTEGER NOT NULL CHECK(proof_sequence > 0),
    proof_runtime_epoch TEXT NOT NULL CHECK(length(trim(proof_runtime_epoch)) BETWEEN 1 AND 128),
    created_at_unix_ms INTEGER NOT NULL CHECK(created_at_unix_ms >= 0),
    UNIQUE(operation_id)
);
CREATE INDEX agent_update_confirmation_machine
    ON agent_update_confirmations(machine_id,created_at_unix_ms);
CREATE TRIGGER agent_update_confirmation_immutable_update
BEFORE UPDATE ON agent_update_confirmations
BEGIN SELECT RAISE(ABORT,'Agent update confirmation is immutable'); END;
CREATE TRIGGER agent_update_confirmation_immutable_delete
BEFORE DELETE ON agent_update_confirmations
BEGIN SELECT RAISE(ABORT,'Agent update confirmation is immutable'); END;

PRAGMA user_version=22;
