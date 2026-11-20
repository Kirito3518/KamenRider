/** Database-backed top-up rate limits (single Next.js node, no Redis). */

import type Database from "better-sqlite3";
import {
  SLIP_VERIFY_RATE_LIMIT,
  TOPUP_CREATE_RATE_LIMIT,
  TOPUP_PENDING_LIMIT,
} from "@/lib/topup/constants";
import { TopupError } from "@/lib/topup/errors";
import { ensureTopupSchema } from "@/lib/topup/schema";

export function enforceCreateRateLimit(
  database: Database.Database,
  userId: string,
  now = Date.now(),
) {
  ensureTopupSchema(database);
  const since = now - TOPUP_CREATE_RATE_LIMIT.windowMs;
  const count = (
    database
      .prepare(
        "SELECT count(*) AS count FROM topup_transactions WHERE user_id = ? AND created_at >= ?",
      )
      .get(userId, since) as { count: number }
  ).count;
  if (count >= TOPUP_CREATE_RATE_LIMIT.max) {
    const retryAfter = Math.max(
      1,
      Math.ceil((since + TOPUP_CREATE_RATE_LIMIT.windowMs - now) / 1000),
    );
    throw new TopupError(
      "rate-limited",
      "คุณสร้างรายการเติมเงินถี่เกินไป กรุณารอสักครู่แล้วลองใหม่",
      429,
      retryAfter,
    );
  }

  const pending = (
    database
      .prepare(
        `SELECT count(*) AS count FROM topup_transactions
         WHERE user_id = ? AND status IN ('pending','processing')`,
      )
      .get(userId) as { count: number }
  ).count;
  if (pending >= TOPUP_PENDING_LIMIT) {
    throw new TopupError(
      "rate-limited",
      "คุณมีรายการที่ยังไม่เสร็จสิ้นหลายรายการ กรุณาทำรายการเดิมให้เสร็จก่อน",
      429,
      30,
    );
  }
}

export function enforceSlipAttemptRateLimit(
  database: Database.Database,
  topupId: string,
  now = Date.now(),
) {
  ensureTopupSchema(database);
  const since = now - SLIP_VERIFY_RATE_LIMIT.windowMs;
  const count = (
    database
      .prepare(
        "SELECT count(*) AS count FROM slip_verification_attempts WHERE topup_id = ? AND created_at >= ?",
      )
      .get(topupId, since) as { count: number }
  ).count;
  if (count >= SLIP_VERIFY_RATE_LIMIT.max) {
    const retryAfter = Math.max(
      1,
      Math.ceil((since + SLIP_VERIFY_RATE_LIMIT.windowMs - now) / 1000),
    );
    throw new TopupError(
      "rate-limited",
      "ตรวจสอบสลิปบ่อยเกินไป กรุณารอสักครู่แล้วลองใหม่",
      429,
      retryAfter,
    );
  }
}

export function countPendingVerificationAttempts(
  database: Database.Database,
  topupId: string,
) {
  ensureTopupSchema(database);
  return (
    database
      .prepare(
        "SELECT count(*) AS count FROM slip_verification_attempts WHERE topup_id = ?",
      )
      .get(topupId) as { count: number }
  ).count;
}
