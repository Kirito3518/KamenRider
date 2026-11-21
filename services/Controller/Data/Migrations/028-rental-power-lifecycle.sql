-- Machine rental lifecycle: admin availability mode, post-lease clearing window, and
-- the customer-triggered power request on a prepared lease.
ALTER TABLE machines ADD COLUMN availability TEXT NOT NULL DEFAULT 'rentable'
    CHECK (availability IN ('rentable','maintenance','disabled'));
ALTER TABLE machines ADD COLUMN clearing_until_unix_ms INTEGER;
ALTER TABLE leases ADD COLUMN wake_requested_at_unix_ms INTEGER;
ALTER TABLE leases ADD COLUMN wake_request_id TEXT;
PRAGMA user_version=28;
