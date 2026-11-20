import type Database from "better-sqlite3";
import {
  TOPUP_MAX_SATANG,
  TOPUP_MIN_SATANG,
} from "@/lib/topup/constants";
import { TopupError } from "@/lib/topup/errors";
import { requireTopupRow, type TopupRow } from "@/lib/topup/transactions";

const UUID_PATTERN =
  /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i;
const VOUCHER_PATTERN = /^[A-Za-z0-9_-]{1,128}$/;

export function assertRequestId(value: unknown): string {
  if (typeof value !== "string" || !UUID_PATTERN.test(value.trim())) {
    throw new TopupError("invalid-request", "รหัสคำขอไม่ถูกต้อง", 400);
  }
  return value.trim().toLowerCase();
}

export function assertTopupAmount(value: unknown): number {
  if (!Number.isSafeInteger(value)) {
    throw new TopupError(
      "amount-out-of-range",
      "จำนวนเงินต้องเป็นจำนวนเต็มสตางค์",
      400,
    );
  }
  const amount = value as number;
  if (amount < TOPUP_MIN_SATANG || amount > TOPUP_MAX_SATANG) {
    throw new TopupError(
      "amount-out-of-range",
      `จำนวนเงินต้องอยู่ระหว่าง ${TOPUP_MIN_SATANG / 100} – ${TOPUP_MAX_SATANG / 100} บาท`,
      400,
    );
  }
  return amount;
}

export function readUserBalance(
  database: Database.Database,
  userId: string,
): number {
  const row = database
    .prepare("SELECT balanceSatang FROM user WHERE id = ?")
    .get(userId) as { balanceSatang: number } | undefined;
  if (!row) throw new TopupError("account-not-found", "ไม่พบบัญชีสมาชิก", 409);
  return row.balanceSatang;
}

/**
 * Accepts a full TrueMoney angpao link or a raw voucher id. Only the voucher id
 * is ever sent to the provider; the user-supplied host is never fetched.
 */
export function parseTrueMoneyVoucher(value: unknown): string {
  if (typeof value !== "string") {
    throw new TopupError("invalid-request", "กรุณาระบุลิงก์ซองอั่งเปา", 400);
  }
  const text = value.trim();
  if (!text || text.length > 512) {
    throw new TopupError("invalid-request", "ลิงก์ซองอั่งเปาไม่ถูกต้อง", 400);
  }
  if (VOUCHER_PATTERN.test(text)) return text;
  let url: URL;
  try {
    url = new URL(text);
  } catch {
    throw new TopupError(
      "invalid-request",
      "ลิงก์ซองอั่งเปาไม่ถูกต้อง ตัวอย่าง: https://gift.truemoney.com/campaign/?v=...",
      400,
    );
  }
  if (
    url.protocol !== "https:" ||
    url.hostname !== "gift.truemoney.com" ||
    !url.pathname.startsWith("/campaign")
  ) {
    throw new TopupError(
      "invalid-request",
      "รองรับเฉพาะลิงก์ซองอั่งเปาจาก gift.truemoney.com",
      400,
    );
  }
  const voucher = url.searchParams.get("v")?.trim() ?? "";
  if (!VOUCHER_PATTERN.test(voucher)) {
    throw new TopupError(
      "invalid-request",
      "ไม่พบรหัสซองอั่งเปาในลิงก์",
      400,
    );
  }
  return voucher;
}

export function requestFingerprint(parts: Record<string, string | number | null>) {
  return JSON.stringify(parts);
}

/** Returns a top-up only when it belongs to the session user (404 otherwise). */
export function requireOwnedTopup(
  database: Database.Database,
  topupId: string,
  userId: string,
): TopupRow {
  const row = requireTopupRow(database, topupId);
  if (row.user_id !== userId) {
    throw new TopupError("transaction-not-found", "ไม่พบรายการเติมเงิน", 404);
  }
  return row;
}
