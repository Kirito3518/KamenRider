/**
 * Admin review queue for top-ups whose automatic slip verification could not
 * reach a safe decision. Approving re-checks the selected receiver account
 * against the slip snapshot first: a mismatch auto-rejects the row, while a
 * match (or an account the Admin manually inspected because the provider hid
 * too much) is credited through the same money CAS as automatic credit.
 */

import type Database from "better-sqlite3";
import { TopupError } from "@/lib/topup/errors";
import { ensureTopupSchema } from "@/lib/topup/schema";
import {
  getTopupDetail,
  markTopupFailed,
  requireTopupRow,
  type TopupRow,
} from "@/lib/topup/transactions";
import { creditReviewedTopup } from "@/lib/topup/wallet";
import { readUserBalance } from "@/lib/topup/services/shared";
import { checkRecipient, type CompareOutcome } from "@/lib/topup/verify";
import type { NormalizedSlip } from "@/lib/topup/providers/types";
import type { TopupSlipResponse } from "@/lib/topup/types";

export type ManualReviewAction = "approve" | "reject";

export type ReviewComparison = {
  outcome: CompareOutcome;
  amount: CompareOutcome;
  recipient: CompareOutcome;
  message: string;
};

export type AdminReviewRequest = {
  action: ManualReviewAction;
  reason?: string | null;
};

/** Rows the Admin may approve/reject: a slip exists but no automatic verdict. */
export function isReviewableTopup(row: TopupRow): boolean {
  return (
    row.status === "processing" &&
    row.slip_image_path !== null &&
    (row.verification_status === "unknown" ||
      row.verification_status === "error")
  );
}

/** Rebuild the parsed slip facts persisted when verification stalled. */
export function reviewSlipFromRow(row: TopupRow): NormalizedSlip {
  return {
    provider: row.slip_provider ?? "easyslip",
    transRef: row.slip_trans_ref,
    amountSatang: row.slip_amount_satang,
    dateIso: row.slip_date,
    senderName: row.slip_sender_name,
    receiverName: row.slip_receiver_name,
    receiverBankCode: row.slip_bank_code,
    receiverBankName: null,
    receiverAccountNumber: row.slip_receiver_account,
    receiverProxyType: null,
    receiverProxyValue: row.slip_receiver_proxy,
  };
}

/**
 * Compare the receiver snapshot the customer paid to against the receiver the
 * slip names. Mismatch auto-rejects; insufficient leaves the final call to the
 * Admin who can see the slip image.
 */
export function compareReview(row: TopupRow): ReviewComparison {
  const amount: CompareOutcome =
    row.slip_amount_satang === null || row.requested_satang === null
      ? "insufficient"
      : row.slip_amount_satang === row.requested_satang
        ? "match"
        : "mismatch";
  const recipient = checkRecipient(row, reviewSlipFromRow(row));
  if (amount === "mismatch" || recipient.outcome === "mismatch") {
    return {
      outcome: "mismatch",
      amount,
      recipient: recipient.outcome,
      message:
        amount === "mismatch"
          ? "ยอดเงินในสลิปไม่ตรงกับรายการ"
          : recipient.message,
    };
  }
  if (amount === "match" && recipient.outcome === "match") {
    return {
      outcome: "match",
      amount,
      recipient: recipient.outcome,
      message: "บัญชีผู้รับและยอดเงินตรงกับรายการ",
    };
  }
  return {
    outcome: "insufficient",
    amount,
    recipient: recipient.outcome,
    message:
      recipient.outcome === "insufficient"
        ? recipient.message
        : "ข้อมูลยอดเงินในสลิปไม่พอสำหรับการยืนยันอัตโนมัติ",
  };
}

function responseFor(
  database: Database.Database,
  row: TopupRow,
  outcome: TopupSlipResponse["outcome"],
  message: string,
): TopupSlipResponse {
  return {
    topup: getTopupDetail(database, row),
    balanceSatang: readUserBalance(database, row.user_id),
    outcome,
    message,
  };
}

export function manualReviewTopup(
  database: Database.Database,
  _actor: { id: string; name: string },
  topupId: string,
  input: AdminReviewRequest,
): TopupSlipResponse {
  ensureTopupSchema(database);
  if (input.action !== "approve" && input.action !== "reject")
    throw new TopupError("invalid-request", "คำสั่งตรวจสอบไม่ถูกต้อง", 400);
  const row = requireTopupRow(database, topupId);
  if (row.status === "completed")
    return responseFor(database, row, "completed", "รายการนี้เติมเงินสำเร็จแล้ว");
  if (!isReviewableTopup(row))
    throw new TopupError(
      "transaction-finished",
      "รายการนี้ไม่ได้อยู่ในสถานะรอตรวจสอบ",
      409,
    );

  const reason = input.reason?.trim().slice(0, 300) || null;
  if (input.action === "reject") {
    const message = reason || "ผู้ดูแลปฏิเสธรายการเติมเงินนี้";
    const failed = markTopupFailed(database, {
      topupId: row.id,
      verificationStatus: "rejected",
      verificationCode: "manual_rejected",
      verificationMessage: message,
      failureCode: "manual_rejected",
      failureMessage: message,
      actor: "admin",
    });
    return responseFor(database, failed, "failed", message);
  }

  const comparison = compareReview(row);
  if (comparison.outcome === "mismatch") {
    // The customer's selected receiver does not match the slip: reject now
    // instead of letting an Admin credit the wrong account.
    const failed = markTopupFailed(database, {
      topupId: row.id,
      verificationStatus: "rejected",
      verificationCode:
        comparison.amount === "mismatch" ? "amount_mismatch" : "recipient_mismatch",
      verificationMessage: comparison.message,
      failureCode:
        comparison.amount === "mismatch" ? "amount_mismatch" : "recipient_mismatch",
      failureMessage: comparison.message,
      actor: "admin",
    });
    return responseFor(database, failed, "failed", comparison.message);
  }

  const amountSatang = row.requested_satang ?? row.slip_amount_satang;
  if (amountSatang === null || amountSatang <= 0)
    throw new TopupError(
      "amount-mismatch",
      "ไม่พบจำนวนเงินที่ต้องเติมสำหรับรายการนี้",
      409,
    );

  const note =
    comparison.outcome === "match"
      ? "ผู้ดูแลอนุมัติหลังตรวจสอบสลิป: บัญชีผู้รับและยอดเงินตรงกัน"
      : "ผู้ดูแลอนุมัติหลังตรวจสอบสลิปด้วยตนเอง (ข้อมูลผู้รับจากผู้ให้บริการไม่พอสำหรับยืนยันอัตโนมัติ)";
  const credited = creditReviewedTopup(database, {
    topupId: row.id,
    amountSatang,
    note,
  });
  return responseFor(
    database,
    credited.row,
    "completed",
    "ผู้ดูแลอนุมัติรายการและเพิ่มยอดเงินแล้ว",
  );
}
