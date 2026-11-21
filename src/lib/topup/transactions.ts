/**
 * Top-up transaction persistence and state transitions.
 *
 * Every transition that can affect money is a conditional UPDATE (CAS) inside
 * an IMMEDIATE transaction. External provider calls never run inside these
 * transactions; routes/services call into this module before/after the call.
 */

import type Database from "better-sqlite3";
import { randomUUID } from "node:crypto";
import {
  MAX_VERIFICATION_ATTEMPTS,
  STALE_PROCESSING_MS,
  type TopupChannel,
  type TopupStatus,
  type VerificationStatus,
} from "@/lib/topup/constants";
import { maskAccountNumber, maskPhone, maskPromptpayNumber } from "@/lib/topup/amount";
import { TopupError } from "@/lib/topup/errors";
import { createTopupReference } from "@/lib/topup/reference";
import type { NormalizedSlip } from "@/lib/topup/providers/types";
import { ensureTopupSchema } from "@/lib/topup/schema";
import type {
  TopupDetail,
  TopupEntry,
  TopupEventView,
} from "@/lib/topup/types";

export type TopupRow = {
  id: string;
  reference: string;
  user_id: string;
  user_name: string;
  user_email: string;
  channel: TopupChannel;
  status: TopupStatus;
  requested_satang: number | null;
  credited_satang: number;
  currency: string;
  idempotency_key: string;
  request_fingerprint: string;
  qr_payload: string | null;
  qr_image_data_url: string | null;
  qr_expires_at: number | null;
  expires_at: number | null;
  bank_account_id: string | null;
  bank_code: string | null;
  bank_name: string | null;
  receiver_account_number: string | null;
  receiver_account_name: string | null;
  promptpay_type: "msisdn" | "natId" | null;
  promptpay_number: string | null;
  truemoney_phone: string | null;
  truemoney_voucher_id: string | null;
  provider_request_id: string | null;
  slip_provider: "easyslip" | "slipok" | null;
  slip_trans_ref: string | null;
  slip_image_path: string | null;
  slip_image_mime: string | null;
  slip_image_hash: string | null;
  slip_amount_satang: number | null;
  slip_sender_name: string | null;
  slip_receiver_name: string | null;
  slip_receiver_account: string | null;
  slip_receiver_proxy: string | null;
  slip_bank_code: string | null;
  slip_date: string | null;
  verification_status: VerificationStatus;
  verification_code: string | null;
  verification_message: string | null;
  verified_at: number | null;
  verified_recovered: number;
  verification_started_at: number | null;
  verification_attempts: number;
  processing_token: string | null;
  failure_code: string | null;
  failure_message: string | null;
  completed_at: number | null;
  created_at: number;
  updated_at: number;
};

export type TopupEventRow = {
  id: string;
  topup_id: string;
  from_status: TopupStatus | null;
  to_status: TopupStatus;
  reason: string | null;
  actor: "system" | "user" | "admin";
  created_at: number;
};

export type AttemptRow = {
  id: string;
  topup_id: string;
  attempt_no: number;
  provider: "easyslip" | "slipok" | "truemoney";
  outcome: string;
  http_status: number | null;
  error_code: string | null;
  error_message: string | null;
  trans_ref: string | null;
  amount_satang: number | null;
  duration_ms: number | null;
  retry_after_seconds: number | null;
  request_summary: string | null;
  response_excerpt: string | null;
  created_at: number;
};

export type CreateTopupInput = {
  userId: string;
  userName: string;
  userEmail: string;
  channel: TopupChannel;
  requestedSatang: number | null;
  idempotencyKey: string;
  fingerprint: string;
  expiresAt: number | null;
  bank?: {
    id: string;
    code: string;
    name: string;
    accountNumber: string;
    accountName: string;
  };
  promptpay?: {
    type: "msisdn" | "natId";
    number: string;
    accountName: string;
    /** PromptPay payee's bank identity, checked against the slip receiver. */
    bankCode?: string;
    bankName?: string;
    accountNumber?: string;
  };
  truemoney?: {
    phone: string;
    voucherId: string;
  };
  now?: number;
};

function selectRow(database: Database.Database, where: string, value: string) {
  return database
    .prepare(`SELECT * FROM topup_transactions WHERE ${where} = ?`)
    .get(value) as TopupRow | undefined;
}

export function findTopupRow(
  database: Database.Database,
  topupId: string,
): TopupRow | null {
  ensureTopupSchema(database);
  return selectRow(database, "id", topupId) ?? null;
}

export function findTopupRowByReference(
  database: Database.Database,
  reference: string,
): TopupRow | null {
  ensureTopupSchema(database);
  return selectRow(database, "reference", reference) ?? null;
}

export function requireTopupRow(
  database: Database.Database,
  topupId: string,
): TopupRow {
  const row = findTopupRow(database, topupId);
  if (!row) {
    throw new TopupError("transaction-not-found", "ไม่พบรายการเติมเงิน", 404);
  }
  return row;
}

function isUniqueError(error: unknown) {
  return error instanceof Error && /UNIQUE constraint failed/i.test(error.message);
}

function uniqueTarget(error: unknown): string {
  return error instanceof Error ? error.message : "";
}

export function recordTopupEvent(
  database: Database.Database,
  input: {
    topupId: string;
    fromStatus: TopupStatus | null;
    toStatus: TopupStatus;
    reason: string | null;
    actor: "system" | "user" | "admin";
    now?: number;
  },
) {
  database
    .prepare(
      `INSERT INTO topup_events(id, topup_id, from_status, to_status, reason, actor, created_at)
       VALUES (?,?,?,?,?,?,?)`,
    )
    .run(
      randomUUID(),
      input.topupId,
      input.fromStatus,
      input.toStatus,
      input.reason?.slice(0, 300) ?? null,
      input.actor,
      input.now ?? Date.now(),
    );
}

export function createTopup(
  database: Database.Database,
  input: CreateTopupInput,
): { row: TopupRow; repeated: boolean } {
  ensureTopupSchema(database);
  const now = input.now ?? Date.now();
  return database
    .transaction((): { row: TopupRow; repeated: boolean } => {
      const existing = database
        .prepare(
          "SELECT * FROM topup_transactions WHERE user_id = ? AND idempotency_key = ?",
        )
        .get(input.userId, input.idempotencyKey) as TopupRow | undefined;
      if (existing) {
        if (existing.request_fingerprint !== input.fingerprint) {
          throw new TopupError(
            "idempotency-conflict",
            "รหัสคำขอซ้ำกับรายการอื่น กรุณาเริ่มรายการใหม่",
            409,
          );
        }
        return { row: existing, repeated: true };
      }

      let lastError: unknown;
      for (let attempt = 0; attempt < 5; attempt += 1) {
        const id = randomUUID();
        const reference = createTopupReference(now);
        try {
          database
            .prepare(
              `INSERT INTO topup_transactions (
                 id, reference, user_id, user_name, user_email, channel, status,
                 requested_satang, idempotency_key, request_fingerprint, expires_at,
                 bank_account_id, bank_code, bank_name, receiver_account_number,
                 receiver_account_name, promptpay_type, promptpay_number,
                 truemoney_phone, truemoney_voucher_id, created_at, updated_at
               ) VALUES (
                 ?,?,?,?,?,?, 'pending', ?,?,?,?,?,?,?,?,?,?,?,?,?,?,?
               )`,
            )
            .run(
              id,
              reference,
              input.userId,
              input.userName.slice(0, 120),
              input.userEmail.slice(0, 200),
              input.channel,
              input.requestedSatang,
              input.idempotencyKey,
              input.fingerprint.slice(0, 500),
              input.expiresAt,
              input.bank?.id ?? null,
              input.bank?.code ?? input.promptpay?.bankCode ?? null,
              input.bank?.name ?? input.promptpay?.bankName ?? null,
              input.bank?.accountNumber ?? input.promptpay?.accountNumber ?? null,
              input.bank?.accountName ?? input.promptpay?.accountName ?? null,
              input.promptpay?.type ?? null,
              input.promptpay?.number ?? null,
              input.truemoney?.phone ?? null,
              input.truemoney?.voucherId ?? null,
              now,
              now,
            );
          recordTopupEvent(database, {
            topupId: id,
            fromStatus: null,
            toStatus: "pending",
            reason: "สร้างรายการเติมเงิน",
            actor: "user",
            now,
          });
          const row = selectRow(database, "id", id);
          if (!row) throw new TopupError("internal-error", "สร้างรายการไม่สำเร็จ", 500);
          return { row, repeated: false };
        } catch (error) {
          lastError = error;
          if (!isUniqueError(error)) throw error;
          const target = uniqueTarget(error);
          if (target.includes("truemoney_voucher_id")) {
            throw new TopupError(
              "duplicate-voucher",
              "ลิงก์ซองอั่งเปานี้ถูกใช้ในระบบแล้ว",
              409,
            );
          }
          if (target.includes("reference")) continue;
          throw error;
        }
      }
      throw lastError ?? new TopupError("internal-error", "สร้างรายการไม่สำเร็จ", 500);
    })
    .immediate();
}

export function updateTopupQr(
  database: Database.Database,
  topupId: string,
  input: {
    qrPayload: string;
    qrImageDataUrl: string;
    qrExpiresAt: number;
    expiresAt: number;
    now?: number;
  },
): TopupRow {
  ensureTopupSchema(database);
  const now = input.now ?? Date.now();
  const result = database
    .prepare(
      `UPDATE topup_transactions
       SET qr_payload = ?, qr_image_data_url = ?, qr_expires_at = ?, expires_at = ?,
           updated_at = ?
       WHERE id = ? AND status = 'pending'`,
    )
    .run(
      input.qrPayload.slice(0, 2_000),
      input.qrImageDataUrl,
      input.qrExpiresAt,
      input.expiresAt,
      now,
      topupId,
    );
  if (result.changes !== 1) {
    throw new TopupError(
      "transaction-finished",
      "รายการนี้สิ้นสุดแล้ว กรุณาเริ่มรายการใหม่",
      409,
    );
  }
  return requireTopupRow(database, topupId);
}

export function expireStaleTopups(
  database: Database.Database,
  now = Date.now(),
): number {
  ensureTopupSchema(database);
  return database
    .transaction(() => {
      const rows = database
        .prepare(
          `SELECT id FROM topup_transactions
           WHERE status = 'pending' AND expires_at IS NOT NULL AND expires_at < ?`,
        )
        .all(now) as { id: string }[];
      if (rows.length === 0) return 0;
      const update = database.prepare(
        `UPDATE topup_transactions SET status = 'expired', processing_token = NULL,
           updated_at = ? WHERE id = ? AND status = 'pending'`,
      );
      for (const row of rows) {
        const result = update.run(now, row.id);
        if (result.changes === 1) {
          recordTopupEvent(database, {
            topupId: row.id,
            fromStatus: "pending",
            toStatus: "expired",
            reason: "รายการหมดอายุ",
            actor: "system",
            now,
          });
        }
      }
      return rows.length;
    })
    .immediate();
}

export function assertTopupNotExpired(
  database: Database.Database,
  row: TopupRow,
  now = Date.now(),
) {
  if (
    row.status === "pending" &&
    row.expires_at !== null &&
    row.expires_at < now
  ) {
    database
      .prepare(
        `UPDATE topup_transactions SET status = 'expired', processing_token = NULL,
           updated_at = ? WHERE id = ? AND status = 'pending'`,
      )
      .run(now, row.id);
    recordTopupEvent(database, {
      topupId: row.id,
      fromStatus: "pending",
      toStatus: "expired",
      reason: "รายการหมดอายุ",
      actor: "system",
      now,
    });
    throw new TopupError(
      "slip-expired",
      "รายการนี้หมดอายุแล้ว กรุณาเริ่มรายการใหม่",
      409,
    );
  }
}

function beginProcessing(
  database: Database.Database,
  topupId: string,
  token: string,
  now: number,
  options: {
    allowStatuses: TopupStatus[];
    staleBefore?: number | null;
    /** Allows a retry immediately when the previous result is explicitly unknown. */
    allowUnknown?: boolean;
    requireSlip?: boolean;
    slip?: {
      path: string;
      mime: string;
      hash: string;
    } | null;
    verificationStatus?: VerificationStatus;
  },
): TopupRow {
  const statusPlaceholders = options.allowStatuses.map(() => "?").join(",");
  const where: string[] = [
    "id = ?",
    `status IN (${statusPlaceholders})`,
    "verification_attempts < ?",
  ];
  const whereValues: unknown[] = [
    topupId,
    ...options.allowStatuses,
    MAX_VERIFICATION_ATTEMPTS,
  ];
  if (options.requireSlip) where.push("slip_image_path IS NOT NULL");
  if (options.staleBefore !== undefined && options.staleBefore !== null) {
    where.push(
      options.allowUnknown
        ? "(verification_status = 'unknown' OR verification_started_at IS NULL OR verification_started_at <= ?)"
        : "(verification_started_at IS NULL OR verification_started_at <= ?)",
    );
    whereValues.push(options.staleBefore);
  }
  if (options.slip) where.push("slip_image_path IS NULL");

  const setValues: unknown[] = [];
  let setSql: string;
  if (options.slip) {
    setSql = `slip_image_path = ?, slip_image_mime = ?, slip_image_hash = ?,
      status = 'processing', verification_status = ?, processing_token = ?,
      verification_started_at = ?, failure_code = NULL, failure_message = NULL,
      verification_attempts = verification_attempts + 1, updated_at = ?`;
    setValues.push(
      options.slip.path,
      options.slip.mime,
      options.slip.hash,
      options.verificationStatus ?? "pending",
      token,
      now,
      now,
    );
  } else {
    setSql = `status = 'processing', verification_status = ?, processing_token = ?,
      verification_started_at = ?, failure_code = NULL, failure_message = NULL,
      verification_attempts = verification_attempts + 1, updated_at = ?`;
    setValues.push(options.verificationStatus ?? "pending", token, now, now);
  }

  const sql = `UPDATE topup_transactions SET ${setSql} WHERE ${where.join(" AND ")}`;
  const result = database.prepare(sql).run(...setValues, ...whereValues);
  if (result.changes !== 1) {
    const current = requireTopupRow(database, topupId);
    if (
      current.status === "processing" &&
      current.processing_token &&
      current.processing_token !== token
    ) {
      throw new TopupError(
        "verification-in-progress",
        "รายการนี้กำลังตรวจสอบอยู่ กรุณารอสักครู่",
        409,
      );
    }
    if (current.status === "pending") {
      throw new TopupError(
        "verification-in-progress",
        "รายการนี้กำลังดำเนินการอยู่ กรุณารอสักครู่",
        409,
      );
    }
    throw new TopupError(
      "transaction-finished",
      "ไม่สามารถเริ่มตรวจสอบรายการนี้ได้",
      409,
    );
  }
  return requireTopupRow(database, topupId);
}

/** First slip upload: stores the file and moves pending → processing. */
export function beginSlipProcessing(
  database: Database.Database,
  input: {
    topupId: string;
    token: string;
    path: string;
    mime: string;
    hash: string;
    now?: number;
  },
): TopupRow {
  ensureTopupSchema(database);
  const now = input.now ?? Date.now();
  try {
    return database
      .transaction(() =>
        beginProcessing(database, input.topupId, input.token, now, {
          allowStatuses: ["pending"],
          requireSlip: false,
          slip: { path: input.path, mime: input.mime, hash: input.hash },
        }),
      )
      .immediate();
  } catch (error) {
    if (isUniqueError(error)) {
      throw new TopupError(
        "duplicate-slip",
        "รูปสลิปนี้เคยถูกใช้ในระบบแล้ว",
        409,
      );
    }
    throw error;
  }
}

/** Retry a slip verification that previously timed out or returned unknown. */
export function beginSlipRetry(
  database: Database.Database,
  input: { topupId: string; token: string; now?: number },
): TopupRow {
  ensureTopupSchema(database);
  const now = input.now ?? Date.now();
  return database
    .transaction(() =>
      beginProcessing(database, input.topupId, input.token, now, {
        allowStatuses: ["processing"],
        staleBefore: now - STALE_PROCESSING_MS,
        allowUnknown: true,
        requireSlip: true,
        verificationStatus: "unknown",
      }),
    )
    .immediate();
}

/** TrueMoney claim call (including an explicit retry after a 429). */
export function beginTrueMoneyProcessing(
  database: Database.Database,
  input: { topupId: string; token: string; now?: number },
): TopupRow {
  ensureTopupSchema(database);
  const now = input.now ?? Date.now();
  // Only a pending row is safe to claim again. A timeout leaves the row in
  // `processing`; retrying that would risk a second claim, so the service
  // rejects it before reaching this function (including for admin/rate-limit
  // retries, where the row was moved back to pending explicitly).
  return database
    .transaction(() =>
      beginProcessing(database, input.topupId, input.token, now, {
        allowStatuses: ["pending"],
        verificationStatus: "pending",
      }),
    )
    .immediate();
}

export function markTopupFailed(
  database: Database.Database,
  input: {
    topupId: string;
    verificationStatus: VerificationStatus;
    verificationCode: string | null;
    verificationMessage: string | null;
    failureCode: string;
    failureMessage: string;
    providerRequestId?: string | null;
    actor?: "system" | "admin" | "user";
    now?: number;
  },
): TopupRow {
  ensureTopupSchema(database);
  const now = input.now ?? Date.now();
  return database
    .transaction(() => {
      const row = requireTopupRow(database, input.topupId);
      if (row.status === "completed" || row.status === "failed" || row.status === "expired") {
        return row;
      }
      database
        .prepare(
          `UPDATE topup_transactions SET status = 'failed',
             verification_status = ?, verification_code = ?, verification_message = ?,
             failure_code = ?, failure_message = ?, processing_token = NULL,
             provider_request_id = COALESCE(?, provider_request_id), updated_at = ?
           WHERE id = ? AND status IN ('pending','processing')`,
        )
        .run(
          input.verificationStatus,
          input.verificationCode?.slice(0, 120) ?? null,
          input.verificationMessage?.slice(0, 300) ?? null,
          input.failureCode.slice(0, 80),
          input.failureMessage.slice(0, 300),
          input.providerRequestId ?? null,
          now,
          input.topupId,
        );
      recordTopupEvent(database, {
        topupId: input.topupId,
        fromStatus: row.status,
        toStatus: "failed",
        reason: input.failureMessage,
        actor: input.actor ?? "system",
        now,
      });
      return requireTopupRow(database, input.topupId);
    })
    .immediate();
}

export function markTopupProcessingUnknown(
  database: Database.Database,
  input: {
    topupId: string;
    code: string;
    message: string;
    providerRequestId?: string | null;
    retryAfterSeconds?: number | null;
    /** Parsed receiver facts kept for the Admin review queue. */
    slip?: NormalizedSlip | null;
    now?: number;
  },
): TopupRow {
  ensureTopupSchema(database);
  const now = input.now ?? Date.now();
  return database
    .transaction(() => {
      const row = requireTopupRow(database, input.topupId);
      if (row.status !== "processing" && row.status !== "pending") return row;
      // The review snapshot must not claim a slip reference another row already
      // owns; the unique index would abort the update. The image hash still
      // protects against a reused slip while this row waits for review.
      let slipTransRef = input.slip?.transRef ?? null;
      if (slipTransRef && input.slip?.provider) {
        const duplicate = database
          .prepare(
            "SELECT id FROM topup_transactions WHERE slip_provider = ? AND slip_trans_ref = ? AND id <> ?",
          )
          .get(input.slip.provider, slipTransRef, input.topupId);
        if (duplicate) slipTransRef = null;
      }
      database
        .prepare(
          `UPDATE topup_transactions SET status = 'processing',
             verification_status = 'unknown', verification_code = ?,
             verification_message = ?, failure_code = ?, failure_message = ?,
             processing_token = NULL, provider_request_id = COALESCE(?, provider_request_id),
             slip_provider = COALESCE(?, slip_provider),
             slip_trans_ref = COALESCE(?, slip_trans_ref),
             slip_amount_satang = COALESCE(?, slip_amount_satang),
             slip_sender_name = COALESCE(?, slip_sender_name),
             slip_receiver_name = COALESCE(?, slip_receiver_name),
             slip_receiver_account = COALESCE(?, slip_receiver_account),
             slip_receiver_proxy = COALESCE(?, slip_receiver_proxy),
             slip_bank_code = COALESCE(?, slip_bank_code),
             slip_date = COALESCE(?, slip_date),
             updated_at = ?
           WHERE id = ? AND status IN ('pending','processing')`,
        )
        .run(
          input.code.slice(0, 120),
          input.message.slice(0, 300),
          input.code.slice(0, 80),
          input.message.slice(0, 300),
          input.providerRequestId ?? null,
          input.slip?.provider ?? null,
          slipTransRef,
          input.slip?.amountSatang ?? null,
          input.slip?.senderName ?? null,
          input.slip?.receiverName ?? null,
          input.slip?.receiverAccountNumber ?? null,
          input.slip?.receiverProxyValue ?? null,
          input.slip?.receiverBankCode ?? null,
          input.slip?.dateIso ?? null,
          now,
          input.topupId,
        );
      recordTopupEvent(database, {
        topupId: input.topupId,
        fromStatus: row.status,
        toStatus: "processing",
        reason: input.message,
        actor: "system",
        now,
      });
      return requireTopupRow(database, input.topupId);
    })
    .immediate();
}

export function markTopupPendingRetry(
  database: Database.Database,
  input: {
    topupId: string;
    code: string;
    message: string;
    newExpiresAt?: number | null;
    providerRequestId?: string | null;
    now?: number;
  },
): TopupRow {
  ensureTopupSchema(database);
  const now = input.now ?? Date.now();
  return database
    .transaction(() => {
      const row = requireTopupRow(database, input.topupId);
      if (row.status !== "processing" && row.status !== "pending") return row;
      database
        .prepare(
          `UPDATE topup_transactions SET status = 'pending',
             verification_status = 'pending', failure_code = ?, failure_message = ?,
             processing_token = NULL, provider_request_id = COALESCE(?, provider_request_id),
             expires_at = COALESCE(?, expires_at), updated_at = ?
           WHERE id = ? AND status IN ('pending','processing')`,
        )
        .run(
          input.code.slice(0, 80),
          input.message.slice(0, 300),
          input.providerRequestId ?? null,
          input.newExpiresAt ?? null,
          now,
          input.topupId,
        );
      recordTopupEvent(database, {
        topupId: input.topupId,
        fromStatus: row.status,
        toStatus: "pending",
        reason: input.message,
        actor: "system",
        now,
      });
      return requireTopupRow(database, input.topupId);
    })
    .immediate();
}

export function recordAttempt(
  database: Database.Database,
  input: {
    topupId: string;
    attemptNo: number;
    provider: "easyslip" | "slipok" | "truemoney";
    outcome: string;
    httpStatus: number | null;
    errorCode: string | null;
    errorMessage: string | null;
    transRef: string | null;
    amountSatang: number | null;
    durationMs: number | null;
    retryAfterSeconds?: number | null;
    requestSummary?: string | null;
    responseExcerpt?: string | null;
    now?: number;
  },
) {
  ensureTopupSchema(database);
  database
    .prepare(
      `INSERT INTO slip_verification_attempts
       (id, topup_id, attempt_no, provider, outcome, http_status, error_code,
        error_message, trans_ref, amount_satang, duration_ms, retry_after_seconds,
        request_summary, response_excerpt, created_at)
       VALUES (?,?,?,?,?,?,?,?,?,?,?,?,?,?,?)`,
    )
    .run(
      randomUUID(),
      input.topupId,
      input.attemptNo,
      input.provider,
      input.outcome.slice(0, 40),
      input.httpStatus,
      input.errorCode?.slice(0, 120) ?? null,
      input.errorMessage?.slice(0, 300) ?? null,
      input.transRef?.slice(0, 160) ?? null,
      input.amountSatang,
      input.durationMs,
      input.retryAfterSeconds ?? null,
      input.requestSummary?.slice(0, 2_000) ?? null,
      input.responseExcerpt?.slice(0, 32_000) ?? null,
      input.now ?? Date.now(),
    );
}

export function getTopupEvents(
  database: Database.Database,
  topupId: string,
): TopupEventView[] {
  ensureTopupSchema(database);
  const rows = database
    .prepare(
      "SELECT * FROM topup_events WHERE topup_id = ? ORDER BY created_at, rowid",
    )
    .all(topupId) as TopupEventRow[];
  return rows.map((row) => ({
    id: row.id,
    fromStatus: row.from_status,
    toStatus: row.to_status,
    reason: row.reason,
    actor: row.actor,
    createdAt: row.created_at,
  }));
}

export function getTopupAttempts(
  database: Database.Database,
  topupId: string,
): AttemptRow[] {
  ensureTopupSchema(database);
  return database
    .prepare(
      "SELECT * FROM slip_verification_attempts WHERE topup_id = ? ORDER BY attempt_no",
    )
    .all(topupId) as AttemptRow[];
}

export function topupEntryFromRow(row: TopupRow): TopupEntry {
  return {
    id: row.id,
    reference: row.reference,
    channel: row.channel,
    status: row.status,
    requestedSatang: row.requested_satang,
    creditedSatang: row.credited_satang,
    createdAt: row.created_at,
    completedAt: row.completed_at,
    expiresAt: row.expires_at,
    verificationStatus: row.verification_status,
    failureCode: row.failure_code,
    failureMessage:
      row.status === "failed"
        ? row.failure_message || row.verification_message
        : null,
    bankName: row.bank_name,
    bankCode: row.bank_code,
    receiverAccountName: row.receiver_account_name,
  };
}

export function topupDetailFromRow(
  row: TopupRow,
  events: TopupEventView[],
  now = Date.now(),
): TopupDetail {
  const stale =
    row.verification_started_at !== null &&
    row.verification_started_at <= now - STALE_PROCESSING_MS;
  const canResume =
    row.status === "pending" &&
    (row.channel === "promptpay" || row.channel === "bank_transfer") &&
    (row.expires_at === null || row.expires_at > now);
  const canRetry =
    (row.status === "processing" &&
      row.slip_image_path !== null &&
      row.verification_attempts < MAX_VERIFICATION_ATTEMPTS &&
      (row.verification_status === "unknown" || stale)) ||
    (row.channel === "truemoney" &&
      row.status === "pending" &&
      row.failure_code === "RATE_LIMITED");
  return {
    ...topupEntryFromRow(row),
    updatedAt: row.updated_at,
    qrImageDataUrl: row.qr_image_data_url,
    qrPayload: row.qr_payload,
    qrExpiresAt: row.qr_expires_at,
    receiverAccountNumber: row.receiver_account_number,
    promptpayType: row.promptpay_type,
    promptpayNumberMasked: row.promptpay_number
      ? maskPromptpayNumber(row.promptpay_type ?? "msisdn", row.promptpay_number)
      : null,
    truemoneyPhoneMasked: row.truemoney_phone
      ? maskPhone(row.truemoney_phone)
      : null,
    slipProvider: row.slip_provider,
    slipTransRef: row.slip_trans_ref,
    slipAmountSatang: row.slip_amount_satang,
    slipSenderName: row.slip_sender_name,
    slipReceiverName: row.slip_receiver_name,
    slipReceiverAccountMasked: row.slip_receiver_account
      ? maskAccountNumber(row.slip_receiver_account)
      : null,
    slipReceiverProxyMasked: row.slip_receiver_proxy
      ? maskAccountNumber(row.slip_receiver_proxy)
      : null,
    slipBankCode: row.slip_bank_code,
    slipImageUrl: row.slip_image_path ? `/api/topups/${row.id}/slip` : null,
    verifiedAt: row.verified_at,
    verificationCode: row.verification_code,
    verificationMessage: row.verification_message,
    attemptsCount: row.verification_attempts,
    canResume,
    canRetry,
    events,
  };
}

export function getTopupDetail(
  database: Database.Database,
  row: TopupRow,
  now = Date.now(),
): TopupDetail {
  return topupDetailFromRow(row, getTopupEvents(database, row.id), now);
}
