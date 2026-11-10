DROP INDEX agent_update_one_active_per_machine;
DROP INDEX agent_update_unstarted_expiry;
ALTER TABLE agent_update_operations RENAME TO agent_update_operations_legacy;

CREATE TABLE agent_maintenance_chains (
    chain_id TEXT PRIMARY KEY,
    machine_id TEXT NOT NULL REFERENCES machines(machine_id),
    current_head_operation_id TEXT NOT NULL,
    generation INTEGER NOT NULL CHECK(generation > 0),
    held INTEGER NOT NULL CHECK(held IN (0,1)),
    inherited INTEGER NOT NULL CHECK(inherited IN (0,1)),
    created_at_unix_ms INTEGER NOT NULL,
    updated_at_unix_ms INTEGER NOT NULL,
    UNIQUE(chain_id,machine_id),
    FOREIGN KEY(chain_id,current_head_operation_id)
        REFERENCES agent_update_operations(chain_id,operation_id) DEFERRABLE INITIALLY DEFERRED
);
CREATE UNIQUE INDEX agent_maintenance_one_held_per_machine
    ON agent_maintenance_chains(machine_id) WHERE held=1;

CREATE TABLE agent_update_operations (
    operation_id TEXT PRIMARY KEY,
    machine_id TEXT NOT NULL REFERENCES machines(machine_id),
    chain_id TEXT NOT NULL,
    parent_operation_id TEXT,
    release_version TEXT NOT NULL REFERENCES agent_releases(version),
    target_version TEXT NOT NULL,
    target_package_sha256 TEXT NOT NULL,
    target_runtime TEXT CHECK(target_runtime IS NULL OR target_runtime IN ('win-x64','win-arm64')),
    target_protocol INTEGER CHECK(target_protocol IS NULL OR target_protocol=2),
    previous_version TEXT,
    source_version TEXT,
    source_evidence_digest TEXT,
    backup_owner_operation_id TEXT,
    backup_digest TEXT,
    status TEXT NOT NULL CHECK(status IN ('awaitingInstaller','installing','awaitingHeartbeat','rollingBack','completed','rolledBack','expired','failed','superseded')),
    ticket_hash TEXT NOT NULL UNIQUE,
    ticket_generation INTEGER NOT NULL,
    ticket_purpose TEXT NOT NULL CHECK(ticket_purpose IN ('legacy','apply','recovery')),
    ticket_expires_at_unix_ms INTEGER NOT NULL,
    ticket_started_at_unix_ms INTEGER,
    started_at_unix_ms INTEGER,
    created_at_unix_ms INTEGER NOT NULL,
    updated_at_unix_ms INTEGER NOT NULL,
    ticket_package_downloaded_at_unix_ms INTEGER,
    CHECK((source_version IS NULL) = (source_evidence_digest IS NULL)),
    CHECK((backup_owner_operation_id IS NULL) = (backup_digest IS NULL)),
    CHECK(source_version IS NULL OR (length(trim(source_version))>0 AND length(source_evidence_digest)=64 AND source_evidence_digest NOT GLOB '*[^0-9a-fA-F]*')),
    CHECK(backup_digest IS NULL OR (length(backup_digest)=64 AND backup_digest NOT GLOB '*[^0-9a-fA-F]*')),
    UNIQUE(chain_id,operation_id),
    FOREIGN KEY(chain_id,parent_operation_id) REFERENCES agent_update_operations(chain_id,operation_id),
    FOREIGN KEY(chain_id,backup_owner_operation_id) REFERENCES agent_update_operations(chain_id,operation_id),
    FOREIGN KEY(chain_id,machine_id) REFERENCES agent_maintenance_chains(chain_id,machine_id) DEFERRABLE INITIALLY DEFERRED
);
CREATE INDEX agent_update_unstarted_expiry
    ON agent_update_operations(status,ticket_expires_at_unix_ms,started_at_unix_ms);

INSERT INTO agent_maintenance_chains
    (chain_id,machine_id,current_head_operation_id,generation,held,inherited,created_at_unix_ms,updated_at_unix_ms)
SELECT 'legacy-' || operation_id,machine_id,operation_id,1,
    CASE WHEN status IN ('completed','rolledBack','expired') THEN 0 ELSE 1 END,
    1,created_at_unix_ms,updated_at_unix_ms FROM agent_update_operations_legacy;

INSERT INTO agent_update_operations
    (operation_id,machine_id,chain_id,release_version,target_version,target_package_sha256,previous_version,status,
     ticket_hash,ticket_generation,ticket_purpose,ticket_expires_at_unix_ms,ticket_started_at_unix_ms,
     started_at_unix_ms,created_at_unix_ms,updated_at_unix_ms,ticket_package_downloaded_at_unix_ms)
SELECT operation_id,machine_id,'legacy-' || operation_id,release_version,target_version,target_package_sha256,previous_version,status,
    ticket_hash,0,'legacy',ticket_expires_at_unix_ms,ticket_started_at_unix_ms,
    started_at_unix_ms,created_at_unix_ms,updated_at_unix_ms,ticket_package_downloaded_at_unix_ms
FROM agent_update_operations_legacy;
DROP TABLE agent_update_operations_legacy;

CREATE TABLE agent_update_audit (
    audit_id TEXT PRIMARY KEY,
    chain_id TEXT NOT NULL REFERENCES agent_maintenance_chains(chain_id),
    operation_id TEXT NOT NULL,
    actor_id TEXT NOT NULL,
    request_id TEXT NOT NULL,
    event_type TEXT NOT NULL,
    old_binding_json TEXT,
    new_binding_json TEXT NOT NULL,
    created_at_unix_ms INTEGER NOT NULL,
    UNIQUE(actor_id,request_id),
    FOREIGN KEY(chain_id,operation_id) REFERENCES agent_update_operations(chain_id,operation_id)
);
CREATE TRIGGER agent_update_audit_immutable_update BEFORE UPDATE ON agent_update_audit
BEGIN SELECT RAISE(ABORT,'Agent update audit is append-only'); END;
CREATE TRIGGER agent_update_audit_immutable_delete BEFORE DELETE ON agent_update_audit
BEGIN SELECT RAISE(ABORT,'Agent update audit is append-only'); END;
CREATE TRIGGER agent_update_operation_identity_immutable
BEFORE UPDATE OF operation_id,machine_id,chain_id,parent_operation_id,release_version,target_version,target_package_sha256,target_runtime,target_protocol,previous_version ON agent_update_operations
BEGIN SELECT RAISE(ABORT,'Agent update operation identity is immutable'); END;
CREATE TRIGGER agent_update_operation_provenance_immutable
BEFORE UPDATE OF source_version,source_evidence_digest,backup_owner_operation_id,backup_digest ON agent_update_operations
WHEN (OLD.source_version IS NOT NULL AND (NEW.source_version IS NOT OLD.source_version OR NEW.source_evidence_digest IS NOT OLD.source_evidence_digest))
  OR (OLD.backup_owner_operation_id IS NOT NULL AND (NEW.backup_owner_operation_id IS NOT OLD.backup_owner_operation_id OR NEW.backup_digest IS NOT OLD.backup_digest))
BEGIN SELECT RAISE(ABORT,'Agent update provenance is immutable'); END;
CREATE TRIGGER agent_update_start_immutable
BEFORE UPDATE OF started_at_unix_ms ON agent_update_operations
WHEN OLD.started_at_unix_ms IS NOT NULL AND NEW.started_at_unix_ms IS NOT OLD.started_at_unix_ms
BEGIN SELECT RAISE(ABORT,'Agent update start is immutable'); END;
CREATE TRIGGER agent_update_superseded_insert_guard
BEFORE INSERT ON agent_update_operations WHEN NEW.status='superseded'
BEGIN SELECT RAISE(ABORT,'Superseded history requires a handoff'); END;
CREATE TRIGGER agent_maintenance_chain_identity_immutable
BEFORE UPDATE OF chain_id,machine_id,inherited ON agent_maintenance_chains
BEGIN SELECT RAISE(ABORT,'Agent maintenance chain identity is immutable'); END;
CREATE TRIGGER agent_maintenance_generation_guard
BEFORE UPDATE OF generation,current_head_operation_id ON agent_maintenance_chains
WHEN NEW.generation < OLD.generation OR (NEW.current_head_operation_id != OLD.current_head_operation_id AND (OLD.held != 1 OR NEW.held != 1 OR NEW.generation <= OLD.generation OR NOT EXISTS(
    SELECT 1 FROM agent_update_operations successor WHERE successor.operation_id=NEW.current_head_operation_id AND successor.chain_id=OLD.chain_id AND successor.parent_operation_id=OLD.current_head_operation_id)))
BEGIN SELECT RAISE(ABORT,'Invalid Agent maintenance generation'); END;
CREATE TRIGGER agent_maintenance_close_guard
BEFORE UPDATE OF held ON agent_maintenance_chains
WHEN (OLD.held=0 AND NEW.held=1) OR (OLD.held=1 AND NEW.held=0 AND (OLD.inherited=1 OR EXISTS(SELECT 1 FROM agent_update_operations o WHERE o.chain_id=OLD.chain_id AND o.started_at_unix_ms IS NOT NULL)
    OR NOT EXISTS(SELECT 1 FROM agent_update_operations o WHERE o.operation_id=OLD.current_head_operation_id AND o.status='expired')))
BEGIN SELECT RAISE(ABORT,'Started or inherited maintenance remains held'); END;
CREATE TRIGGER agent_update_supersession_guard
BEFORE UPDATE OF status ON agent_update_operations
WHEN NEW.status='superseded' AND (OLD.status='superseded' OR NOT EXISTS(
    SELECT 1 FROM agent_maintenance_chains c JOIN agent_update_operations successor ON successor.operation_id=c.current_head_operation_id
    WHERE c.chain_id=OLD.chain_id AND c.held=1 AND successor.parent_operation_id=OLD.operation_id))
BEGIN SELECT RAISE(ABORT,'Supersession requires a held successor'); END;

PRAGMA user_version=14;
