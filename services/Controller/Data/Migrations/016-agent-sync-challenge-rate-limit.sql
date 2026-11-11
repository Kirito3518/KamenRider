ALTER TABLE agent_signed_request_nonces ADD COLUMN retention_second INTEGER NOT NULL DEFAULT 0;
UPDATE agent_signed_request_nonces SET retention_second=signed_second;
ALTER TABLE agent_sync_proof_state ADD COLUMN challenge_window_tick INTEGER;
ALTER TABLE agent_sync_proof_state ADD COLUMN challenge_response_count INTEGER NOT NULL DEFAULT 0
    CHECK(challenge_response_count BETWEEN 0 AND 2 AND ((challenge_response_count=0)=(challenge_window_tick IS NULL)));
PRAGMA user_version=16;
