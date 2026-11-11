ALTER TABLE agent_releases ADD COLUMN target_runtime TEXT NOT NULL DEFAULT 'unknown'
    CHECK(target_runtime IN ('win-x64','win-arm64','unknown'));
ALTER TABLE agent_releases ADD COLUMN agent_update_challenge_protocol INTEGER NOT NULL DEFAULT 1
    CHECK(agent_update_challenge_protocol IN (1,2));

PRAGMA user_version=17;
