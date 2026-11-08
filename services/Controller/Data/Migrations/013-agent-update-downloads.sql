ALTER TABLE agent_update_operations
    ADD COLUMN ticket_package_downloaded_at_unix_ms INTEGER;

PRAGMA user_version=13;
