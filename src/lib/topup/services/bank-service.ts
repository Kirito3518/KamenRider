import type Database from "better-sqlite3";
import { BANK_TRANSFER_EXPIRY_MS } from "@/lib/topup/constants";
import { TopupError } from "@/lib/topup/errors";
import { ensureTopupSchema } from "@/lib/topup/schema";
import {
  getActiveBankAccount,
  getProviderCredentials,
  getTopupSettingsRow,
} from "@/lib/topup/settings";
import {
  createTopup,
  getTopupDetail,
  requireTopupRow,
} from "@/lib/topup/transactions";
import { enforceCreateRateLimit } from "@/lib/topup/rate-limit";
import type {
  TopupCreateResponse,
  TopupCreateOutcome,
} from "@/lib/topup/types";
import {
  assertRequestId,
  assertTopupAmount,
  readUserBalance,
  requestFingerprint,
} from "@/lib/topup/services/shared";
import type { TopupActor } from "@/lib/topup/services/truemoney-service";

export function createBankTransferTopup(
  database: Database.Database,
  actor: TopupActor,
  body: { requestId: unknown; amountSatang: unknown; bankAccountId: unknown },
): TopupCreateResponse {
  ensureTopupSchema(database);
  const settings = getTopupSettingsRow(database);
  if (settings.bank_transfer_enabled !== 1) {
    throw new TopupError(
      "channel-disabled",
      "ช่องทางโอนผ่านบัญชีธนาคารยังไม่เปิดให้บริการ",
      409,
    );
  }
  const credentials = getProviderCredentials(database, settings.slip_provider);
  if (!credentials) {
    throw new TopupError(
      "not-configured",
      "ยังไม่ได้ตั้งค่าผู้ให้บริการตรวจสลิปที่ระบบเลือก",
      409,
    );
  }
  const requestId = assertRequestId(body.requestId);
  const amountSatang = assertTopupAmount(body.amountSatang);
  if (typeof body.bankAccountId !== "string" || !body.bankAccountId.trim()) {
    throw new TopupError("invalid-request", "กรุณาเลือกบัญชีธนาคาร", 400);
  }
  const account = getActiveBankAccount(database, body.bankAccountId.trim());
  if (!account) {
    throw new TopupError(
      "invalid-request",
      "บัญชีธนาคารที่เลือกไม่พร้อมใช้งาน",
      409,
    );
  }
  enforceCreateRateLimit(database, actor.id);
  const now = Date.now();
  const expiresAt = now + BANK_TRANSFER_EXPIRY_MS;
  const fingerprint = requestFingerprint({
    channel: "bank_transfer",
    amountSatang,
    bankAccountId: account.id,
    accountVersion: account.updated_at,
    bankCode: account.bank_code,
    accountNumber: account.account_number,
  });
  const created = createTopup(database, {
    userId: actor.id,
    userName: actor.name,
    userEmail: actor.email,
    channel: "bank_transfer",
    requestedSatang: amountSatang,
    idempotencyKey: requestId,
    fingerprint,
    expiresAt,
    bank: {
      id: account.id,
      code: account.bank_code,
      name: account.bank_name,
      accountNumber: account.account_number,
      accountName: account.account_name,
    },
    now,
  });

  const outcome: TopupCreateOutcome =
    created.row.status === "completed"
      ? "completed"
      : created.row.status === "processing"
        ? "processing"
        : created.row.status === "pending"
          ? "pending"
          : "failed";
  if (created.repeated && (outcome === "failed" || created.row.status === "expired")) {
    throw new TopupError(
      "transaction-finished",
      "รายการนี้สิ้นสุดแล้ว กรุณาเริ่มรายการใหม่",
      409,
    );
  }
  const row = requireTopupRow(database, created.row.id);
  return {
    topup: getTopupDetail(database, row),
    balanceSatang: readUserBalance(database, actor.id),
    repeated: created.repeated,
    outcome,
    message: "สร้างรายการโอนเงินแล้ว กรุณาโอนตามบัญชีที่แสดงและอัปโหลดสลิป",
  };
}
