-- Machine profile (Agent-reported display/hardware data) and the fleet-wide Sunshine
-- management credential.
--
-- Machine profile columns are Agent-reported diagnostics only. They are updated from a
-- signed sync request and must never change approval, rentability or readiness state.
ALTER TABLE machines ADD COLUMN hostname TEXT NOT NULL DEFAULT '';
ALTER TABLE machines ADD COLUMN cpu TEXT NOT NULL DEFAULT '';
ALTER TABLE machines ADD COLUMN gpu TEXT NOT NULL DEFAULT '';
ALTER TABLE machines ADD COLUMN ram_bytes INTEGER NOT NULL DEFAULT 0;
ALTER TABLE machines ADD COLUMN sunshine_credential_revision INTEGER NOT NULL DEFAULT 0;

-- Administrator-set Sunshine management credential. The password is stored only as an
-- authenticated-encryption ciphertext (AES-GCM) produced by the Controller; the plaintext and
-- the encryption key are never written to this database, logs or the repository.
CREATE TABLE sunshine_management_credential (
    singleton INTEGER PRIMARY KEY CHECK(singleton=1),
    username TEXT NOT NULL,
    password_ciphertext BLOB NOT NULL,
    password_nonce BLOB NOT NULL,
    password_tag BLOB NOT NULL,
    revision INTEGER NOT NULL CHECK(revision >= 1),
    updated_at_unix_ms INTEGER NOT NULL CHECK(updated_at_unix_ms >= 0),
    updated_by TEXT NOT NULL
);

PRAGMA user_version=24;
