/**
 * Lazy, versioned schema for the top-up domain in the auth SQLite database.
 *
 * This follows the repository pattern used by rental-payments.ts (idempotent DDL
 * on first use) but adds a tiny migration registry so future columns/tables can
 * be applied in order without touching better-auth tables.
 */

import type Database from "better-sqlite3";

export const TOPUP_SCHEMA_VERSION = 1;

const REGISTRY = `
  CREATE TABLE IF NOT EXISTS topup_schema_migrations (
    version INTEGER PRIMARY KEY,
    name TEXT NOT NULL,
    applied_at INTEGER NOT NULL
  );
`;

const MIGRATION_1 = `
  CREATE TABLE IF NOT EXISTS topup_settings (
    id INTEGER PRIMARY KEY CHECK (id = 1),
    truemoney_enabled INTEGER NOT NULL DEFAULT 0 CHECK (truemoney_enabled IN (0,1)),
    truemoney_phone TEXT,
    promptpay_enabled INTEGER NOT NULL DEFAULT 0 CHECK (promptpay_enabled IN (0,1)),
    promptpay_type TEXT NOT NULL DEFAULT 'msisdn' CHECK (promptpay_type IN ('msisdn','natId')),
    promptpay_number TEXT,
    promptpay_account_name TEXT,
    bank_transfer_enabled INTEGER NOT NULL DEFAULT 0 CHECK (bank_transfer_enabled IN (0,1)),
    coupon_enabled INTEGER NOT NULL DEFAULT 0 CHECK (coupon_enabled IN (0,1)),
    coupon_message TEXT NOT NULL DEFAULT 'คูปองยังไม่เปิดให้บริการในขณะนี้',
    slip_provider TEXT NOT NULL DEFAULT 'easyslip' CHECK (slip_provider IN ('easyslip','slipok')),
    updated_at INTEGER NOT NULL,
    updated_by TEXT,
    CHECK (NOT (promptpay_enabled = 1 AND bank_transfer_enabled = 1))
  );

  CREATE TABLE IF NOT EXISTS topup_provider_settings (
    provider TEXT PRIMARY KEY CHECK (provider IN ('easyslip','slipok')),
    api_key_enc TEXT,
    branch_id TEXT,
    last_test_at INTEGER,
    last_test_ok INTEGER CHECK (last_test_ok IS NULL OR last_test_ok IN (0,1)),
    last_test_message TEXT,
    updated_at INTEGER NOT NULL,
    updated_by TEXT
  );

  CREATE TABLE IF NOT EXISTS topup_bank_accounts (
    id TEXT PRIMARY KEY,
    bank_code TEXT NOT NULL,
    bank_name TEXT NOT NULL,
    account_number TEXT NOT NULL,
    account_name TEXT NOT NULL,
    logo_key TEXT,
    logo_data_url TEXT,
    is_active INTEGER NOT NULL DEFAULT 1 CHECK (is_active IN (0,1)),
    archived_at INTEGER,
    created_at INTEGER NOT NULL,
    updated_at INTEGER NOT NULL,
    CHECK (logo_key IS NOT NULL OR logo_data_url IS NOT NULL)
  );

  CREATE UNIQUE INDEX IF NOT EXISTS topup_bank_accounts_unique
    ON topup_bank_accounts(bank_code, account_number)
    WHERE archived_at IS NULL;
  CREATE INDEX IF NOT EXISTS topup_bank_accounts_active
    ON topup_bank_accounts(is_active, archived_at);

  CREATE TABLE IF NOT EXISTS topup_transactions (
    id TEXT PRIMARY KEY,
    reference TEXT NOT NULL UNIQUE,
    user_id TEXT NOT NULL,
    user_name TEXT NOT NULL,
    user_email TEXT NOT NULL,
    channel TEXT NOT NULL CHECK (channel IN ('truemoney','promptpay','bank_transfer','coupon')),
    status TEXT NOT NULL CHECK (status IN ('pending','processing','completed','failed','expired')),
    requested_satang INTEGER CHECK (requested_satang IS NULL OR requested_satang > 0),
    credited_satang INTEGER NOT NULL DEFAULT 0 CHECK (credited_satang >= 0),
    currency TEXT NOT NULL DEFAULT 'THB',
    idempotency_key TEXT NOT NULL,
    request_fingerprint TEXT NOT NULL,
    qr_payload TEXT,
    qr_image_data_url TEXT,
    qr_expires_at INTEGER,
    expires_at INTEGER,
    bank_account_id TEXT REFERENCES topup_bank_accounts(id),
    bank_code TEXT,
    bank_name TEXT,
    receiver_account_number TEXT,
    receiver_account_name TEXT,
    promptpay_type TEXT,
    promptpay_number TEXT,
    truemoney_phone TEXT,
    truemoney_voucher_id TEXT COLLATE NOCASE,
    provider_request_id TEXT,
    slip_provider TEXT CHECK (slip_provider IS NULL OR slip_provider IN ('easyslip','slipok')),
    slip_trans_ref TEXT,
    slip_image_path TEXT,
    slip_image_mime TEXT,
    slip_image_hash TEXT,
    slip_amount_satang INTEGER,
    slip_sender_name TEXT,
    slip_receiver_name TEXT,
    slip_receiver_account TEXT,
    slip_bank_code TEXT,
    slip_date TEXT,
    verification_status TEXT NOT NULL DEFAULT 'pending'
      CHECK (verification_status IN ('pending','verified','rejected','duplicate','error','unknown')),
    verification_code TEXT,
    verification_message TEXT,
    verified_at INTEGER,
    verified_recovered INTEGER NOT NULL DEFAULT 0 CHECK (verified_recovered IN (0,1)),
    verification_started_at INTEGER,
    verification_attempts INTEGER NOT NULL DEFAULT 0 CHECK (verification_attempts >= 0),
    processing_token TEXT,
    failure_code TEXT,
    failure_message TEXT,
    completed_at INTEGER,
    created_at INTEGER NOT NULL,
    updated_at INTEGER NOT NULL,
    UNIQUE(user_id, idempotency_key)
  );

  CREATE UNIQUE INDEX IF NOT EXISTS topup_transactions_slip_ref
    ON topup_transactions(slip_provider, slip_trans_ref)
    WHERE slip_trans_ref IS NOT NULL;
  CREATE UNIQUE INDEX IF NOT EXISTS topup_transactions_slip_hash
    ON topup_transactions(slip_image_hash)
    WHERE slip_image_hash IS NOT NULL;
  CREATE UNIQUE INDEX IF NOT EXISTS topup_transactions_voucher
    ON topup_transactions(truemoney_voucher_id)
    WHERE truemoney_voucher_id IS NOT NULL;
  CREATE INDEX IF NOT EXISTS topup_transactions_user_created
    ON topup_transactions(user_id, created_at DESC);
  CREATE INDEX IF NOT EXISTS topup_transactions_status_created
    ON topup_transactions(status, created_at);
  CREATE INDEX IF NOT EXISTS topup_transactions_channel_created
    ON topup_transactions(channel, created_at);
  CREATE INDEX IF NOT EXISTS topup_transactions_created
    ON topup_transactions(created_at);
  CREATE INDEX IF NOT EXISTS topup_transactions_completed
    ON topup_transactions(completed_at);
  CREATE INDEX IF NOT EXISTS topup_transactions_bank_account
    ON topup_transactions(bank_account_id);

  CREATE TABLE IF NOT EXISTS wallet_transactions (
    id TEXT PRIMARY KEY,
    user_id TEXT NOT NULL,
    type TEXT NOT NULL CHECK (type IN ('topup','rental_charge','rental_refund','admin_credit','admin_debit','adjustment')),
    amount_satang INTEGER NOT NULL,
    balance_before_satang INTEGER NOT NULL,
    balance_after_satang INTEGER NOT NULL,
    reference_type TEXT NOT NULL,
    reference_id TEXT NOT NULL,
    description TEXT NOT NULL,
    actor_id TEXT,
    created_at INTEGER NOT NULL,
    UNIQUE(reference_type, reference_id, type)
  );
  CREATE INDEX IF NOT EXISTS wallet_transactions_user_created
    ON wallet_transactions(user_id, created_at DESC);
  CREATE INDEX IF NOT EXISTS wallet_transactions_reference
    ON wallet_transactions(reference_type, reference_id);

  CREATE TABLE IF NOT EXISTS slip_verification_attempts (
    id TEXT PRIMARY KEY,
    topup_id TEXT NOT NULL REFERENCES topup_transactions(id),
    attempt_no INTEGER NOT NULL,
    provider TEXT NOT NULL CHECK (provider IN ('easyslip','slipok','truemoney')),
    outcome TEXT NOT NULL,
    http_status INTEGER,
    error_code TEXT,
    error_message TEXT,
    trans_ref TEXT,
    amount_satang INTEGER,
    duration_ms INTEGER,
    retry_after_seconds INTEGER,
    request_summary TEXT,
    response_excerpt TEXT,
    created_at INTEGER NOT NULL,
    UNIQUE(topup_id, attempt_no)
  );
  CREATE INDEX IF NOT EXISTS slip_verification_attempts_topup
    ON slip_verification_attempts(topup_id, created_at);

  CREATE TABLE IF NOT EXISTS topup_events (
    id TEXT PRIMARY KEY,
    topup_id TEXT NOT NULL REFERENCES topup_transactions(id),
    from_status TEXT,
    to_status TEXT NOT NULL,
    reason TEXT,
    actor TEXT NOT NULL CHECK (actor IN ('system','user','admin')),
    created_at INTEGER NOT NULL
  );
  CREATE INDEX IF NOT EXISTS topup_events_topup
    ON topup_events(topup_id, created_at);

  CREATE TABLE IF NOT EXISTS topup_settings_audit (
    id TEXT PRIMARY KEY,
    actor_id TEXT,
    actor_name TEXT,
    action TEXT NOT NULL,
    target_type TEXT NOT NULL,
    target_id TEXT,
    changes_json TEXT NOT NULL,
    created_at INTEGER NOT NULL
  );
  CREATE INDEX IF NOT EXISTS topup_settings_audit_created
    ON topup_settings_audit(created_at DESC);
`;

const schemaReady = new WeakSet<object>();

export function ensureTopupSchema(database: Database.Database) {
  if (schemaReady.has(database)) return;
  database
    .transaction(() => {
      database.exec(REGISTRY);
      const applied = new Set(
        (
          database
            .prepare("SELECT version FROM topup_schema_migrations")
            .all() as { version: number }[]
        ).map((row) => row.version),
      );
      const now = Date.now();
      if (!applied.has(1)) {
        database.exec(MIGRATION_1);
        database
          .prepare(
            "INSERT INTO topup_schema_migrations(version, name, applied_at) VALUES(?,?,?)",
          )
          .run(1, "initial topup schema", now);
      }
      database
        .prepare(
          "INSERT OR IGNORE INTO topup_settings(id, updated_at) VALUES(1, ?)",
        )
        .run(now);
      const insertProvider = database.prepare(
        "INSERT OR IGNORE INTO topup_provider_settings(provider, updated_at) VALUES(?, ?)",
      );
      insertProvider.run("easyslip", now);
      insertProvider.run("slipok", now);
    })
    .immediate();
  schemaReady.add(database);
}

/** Test seam: forget the WeakSet cache after replacing a database connection. */
export function resetTopupSchemaCache(database: Database.Database) {
  schemaReady.delete(database);
}
