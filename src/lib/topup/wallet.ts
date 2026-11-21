/**
 * Atomic top-up credit.
 *
 * The transaction row status, the user's wallet balance, the wallet ledger row
 * and the audit event all commit in one IMMEDIATE transaction. The ledger
 * unique index plus the processing token CAS make a second credit impossible.
 */

import type Database from "better-sqlite3";
import { randomUUID } from "node:crypto";
import { TopupError } from "@/lib/topup/errors";
import type { SlipProvider } from "@/lib/topup/constants";
import { ensureTopupSchema } from "@/lib/topup/schema";
import {
  recordTopupEvent,
  requireTopupRow,
  type TopupRow,
} from "@/lib/topup/transactions";

const CHANNEL_LABEL: Record<string, string> = {
  truemoney: "TrueMoney Angpao",
  promptpay: "PromptPay QR",
  bank_transfer: "โอนผ่านบัญชีธนาคาร",
  coupon: "คูปอง",
};

export type CreditSlipInput = {
  topupId: string;
  processingToken: string;
  provider: SlipProvider;
  transRef: string;
  amountSatang: number;
  verifiedRecovered: boolean;
  providerRequestId: string | null;
  slip: {
    senderName: string | null;
    receiverName: string | null;
    receiverAccount: string | null;
    receiverProxy?: string | null;
    bankCode: string | null;
    dateIso: string | null;
    code: string;
    message: string;
  };
  now?: number;
};

export type CreditTrueMoneyInput = {
  topupId: string;
  processingToken: string;
  amountSatang: number;
  displayName: string | null;
  providerRequestId: string | null;
  now?: number;
};

function assertAmount(amountSatang: number) {
  if (!Number.isSafeInteger(amountSatang) || amountSatang <= 0) {
    throw new TopupError(
      "amount-mismatch",
      "ยอดเงินที่ตรวจสอบไม่ถูกต้อง",
      409,
    );
  }
}

function assertNoDuplicateSlipRef(
  database: Database.Database,
  row: TopupRow,
  provider: SlipProvider,
  transRef: string,
) {
  const existing = database
    .prepare(
      `SELECT id FROM topup_transactions
       WHERE slip_provider = ? AND slip_trans_ref = ? AND id <> ?`,
    )
    .get(provider, transRef, row.id) as { id: string } | undefined;
  if (existing) {
    throw new TopupError(
      "duplicate-slip",
      "สลิปนี้ถูกใช้กับรายการอื่นแล้ว",
      409,
    );
  }
}

function applyCredit(
  database: Database.Database,
  row: TopupRow,
  amountSatang: number,
  now: number,
) {
  const user = database
    .prepare("SELECT balanceSatang FROM user WHERE id = ?")
    .get(row.user_id) as { balanceSatang: number } | undefined;
  if (!user) {
    throw new TopupError("account-not-found", "ไม่พบบัญชีสมาชิก", 409);
  }
  if (!Number.isSafeInteger(user.balanceSatang)) {
    throw new TopupError("internal-error", "ยอดเงินในบัญชีไม่ถูกต้อง", 500);
  }
  const next = user.balanceSatang + amountSatang;
  if (!Number.isSafeInteger(next)) {
    throw new TopupError("internal-error", "ยอดเงินเกินขอบเขตที่ระบบรองรับ", 500);
  }
  const updated = database
    .prepare(
      "UPDATE user SET balanceSatang = ?, updatedAt = ? WHERE id = ? AND balanceSatang = ?",
    )
    .run(next, new Date(now).toISOString(), row.user_id, user.balanceSatang);
  if (updated.changes !== 1) {
    throw new TopupError(
      "internal-error",
      "ยอดเงินถูกแก้ไขระหว่างทำรายการ กรุณาลองใหม่",
      409,
    );
  }
  database
    .prepare(
      `INSERT INTO wallet_transactions
       (id, user_id, type, amount_satang, balance_before_satang,
        balance_after_satang, reference_type, reference_id, description,
        actor_id, created_at)
       VALUES (?,?,?,?,?,?,?,?,?,?,?)`,
    )
    .run(
      randomUUID(),
      row.user_id,
      "topup",
      amountSatang,
      user.balanceSatang,
      next,
      "topup",
      row.id,
      `เติมเงินผ่าน ${CHANNEL_LABEL[row.channel] ?? row.channel}`,
      null,
      now,
    );
  return next;
}

function finalizeRow(
  database: Database.Database,
  row: TopupRow,
  setSql: string,
  setValues: unknown[],
  now: number,
  eventReason: string,
) {
  const result = database
    .prepare(
      `UPDATE topup_transactions SET ${setSql}
       WHERE id = ? AND status = 'processing' AND processing_token = ?`,
    )
    .run(...setValues, row.id, row.processing_token);
  if (result.changes !== 1) {
    const current = requireTopupRow(database, row.id);
    if (current.status === "completed") return current;
    throw new TopupError(
      "verification-in-progress",
      "รายการนี้ถูกดำเนินการจากคำขออื่นแล้ว กรุณารีเฟรช",
      409,
    );
  }
  recordTopupEvent(database, {
    topupId: row.id,
    fromStatus: "processing",
    toStatus: "completed",
    reason: eventReason,
    actor: "system",
    now,
  });
  return requireTopupRow(database, row.id);
}

export function creditSlipTopup(
  database: Database.Database,
  input: CreditSlipInput,
): { row: TopupRow; balanceSatang: number; repeated: boolean } {
  ensureTopupSchema(database);
  assertAmount(input.amountSatang);
  const now = input.now ?? Date.now();
  return database
    .transaction(() => {
      const row = requireTopupRow(database, input.topupId);
      if (row.status === "completed") {
        const balance =
          (
            database
              .prepare("SELECT balanceSatang FROM user WHERE id = ?")
              .get(row.user_id) as { balanceSatang: number } | undefined
          )?.balanceSatang ?? 0;
        return { row, balanceSatang: balance, repeated: true };
      }
      if (row.status !== "processing") {
        throw new TopupError(
          "transaction-finished",
          "รายการนี้ไม่ได้อยู่ในสถานะที่เพิ่มเงินได้",
          409,
        );
      }
      if (row.processing_token !== input.processingToken) {
        throw new TopupError(
          "verification-in-progress",
          "รายการนี้ถูกตรวจสอบจากคำขออื่นอยู่",
          409,
        );
      }
      if (row.requested_satang !== null && row.requested_satang !== input.amountSatang) {
        throw new TopupError(
          "amount-mismatch",
          "ยอดเงินในสลิปไม่ตรงกับรายการ",
          409,
        );
      }
      assertNoDuplicateSlipRef(database, row, input.provider, input.transRef);
      const balanceSatang = applyCredit(database, row, input.amountSatang, now);
      const nextRow = finalizeRow(
        database,
        row,
        `status = 'completed', credited_satang = ?, requested_satang = COALESCE(requested_satang, ?),
         slip_provider = ?, slip_trans_ref = ?, slip_amount_satang = ?,
         slip_sender_name = ?, slip_receiver_name = ?, slip_receiver_account = ?,
         slip_receiver_proxy = ?, slip_bank_code = ?, slip_date = ?, verification_status = 'verified',
         verification_code = ?, verification_message = ?, verified_at = ?,
         verified_recovered = ?, processing_token = NULL,
         provider_request_id = COALESCE(?, provider_request_id),
         completed_at = ?, updated_at = ?`,
        [
          input.amountSatang,
          input.amountSatang,
          input.provider,
          input.transRef,
          input.amountSatang,
          input.slip.senderName,
          input.slip.receiverName,
          input.slip.receiverAccount,
          input.slip.receiverProxy ?? null,
          input.slip.bankCode,
          input.slip.dateIso,
          input.slip.code.slice(0, 120),
          input.slip.message.slice(0, 300),
          now,
          input.verifiedRecovered ? 1 : 0,
          input.providerRequestId,
          now,
          now,
        ],
        now,
        input.slip.message || "ตรวจสอบสลิปผ่าน",
      );
      return { row: nextRow, balanceSatang, repeated: false };
    })
    .immediate();
}

export function creditTrueMoneyTopup(
  database: Database.Database,
  input: CreditTrueMoneyInput,
): { row: TopupRow; balanceSatang: number; repeated: boolean } {
  ensureTopupSchema(database);
  assertAmount(input.amountSatang);
  const now = input.now ?? Date.now();
  return database
    .transaction(() => {
      const row = requireTopupRow(database, input.topupId);
      if (row.status === "completed") {
        const balance =
          (
            database
              .prepare("SELECT balanceSatang FROM user WHERE id = ?")
              .get(row.user_id) as { balanceSatang: number } | undefined
          )?.balanceSatang ?? 0;
        return { row, balanceSatang: balance, repeated: true };
      }
      if (row.status !== "processing") {
        throw new TopupError(
          "transaction-finished",
          "รายการนี้ไม่ได้อยู่ในสถานะที่เพิ่มเงินได้",
          409,
        );
      }
      if (row.processing_token !== input.processingToken) {
        throw new TopupError(
          "verification-in-progress",
          "รายการนี้ถูกตรวจสอบจากคำขออื่นอยู่",
          409,
        );
      }
      const balanceSatang = applyCredit(database, row, input.amountSatang, now);
      const nextRow = finalizeRow(
        database,
        row,
        `status = 'completed', credited_satang = ?, requested_satang = COALESCE(requested_satang, ?),
         verification_status = 'verified', verification_code = 'truemoney_success',
         verification_message = ?, verified_at = ?, processing_token = NULL,
         provider_request_id = COALESCE(?, provider_request_id),
         completed_at = ?, updated_at = ?`,
        [
          input.amountSatang,
          input.amountSatang,
          input.displayName
            ? `รับซองอั่งเปาสำเร็จ (${input.displayName})`
            : "รับซองอั่งเปาสำเร็จ",
          now,
          input.providerRequestId,
          now,
          now,
        ],
        now,
        "รับซองอั่งเปาสำเร็จ",
      );
      return { row: nextRow, balanceSatang, repeated: false };
    })
    .immediate();
}

/**
 * Credit a stuck processing row after an Admin reviewed the slip manually.
 * The selected-account comparison and amount checks happen in the review
 * service before this function is called; this function owns the money CAS.
 */
export function creditReviewedTopup(
  database: Database.Database,
  input: {
    topupId: string;
    amountSatang: number;
    note: string;
    now?: number;
  },
): { row: TopupRow; balanceSatang: number; repeated: boolean } {
  ensureTopupSchema(database);
  assertAmount(input.amountSatang);
  const now = input.now ?? Date.now();
  return database
    .transaction(() => {
      const row = requireTopupRow(database, input.topupId);
      if (row.status === "completed") {
        const balance =
          (
            database
              .prepare("SELECT balanceSatang FROM user WHERE id = ?")
              .get(row.user_id) as { balanceSatang: number } | undefined
          )?.balanceSatang ?? 0;
        return { row, balanceSatang: balance, repeated: true };
      }
      if (row.status !== "processing") {
        throw new TopupError(
          "transaction-finished",
          "รายการนี้ไม่ได้อยู่ในสถานะที่ผู้ดูแลอนุมัติได้",
          409,
        );
      }
      if (row.slip_provider && row.slip_trans_ref) {
        assertNoDuplicateSlipRef(database, row, row.slip_provider, row.slip_trans_ref);
      }
      const balanceSatang = applyCredit(database, row, input.amountSatang, now);
      const result = database
        .prepare(
          `UPDATE topup_transactions SET status = 'completed', credited_satang = ?,
             requested_satang = COALESCE(requested_satang, ?),
             verification_status = 'verified', verification_code = 'manual_approved',
             verification_message = ?, verified_at = ?, verified_recovered = 0,
             processing_token = NULL, completed_at = ?, updated_at = ?
           WHERE id = ? AND status = 'processing'`,
        )
        .run(
          input.amountSatang,
          input.amountSatang,
          input.note.slice(0, 300),
          now,
          now,
          now,
          row.id,
        );
      if (result.changes !== 1) {
        throw new TopupError(
          "verification-in-progress",
          "รายการนี้ถูกดำเนินการจากคำขออื่นแล้ว กรุณารีเฟรช",
          409,
        );
      }
      recordTopupEvent(database, {
        topupId: row.id,
        fromStatus: "processing",
        toStatus: "completed",
        reason: input.note,
        actor: "admin",
        now,
      });
      return {
        row: requireTopupRow(database, row.id),
        balanceSatang,
        repeated: false,
      };
    })
    .immediate();
}
