import type Database from "better-sqlite3";
import {
  TRUEMONEY_PENDING_EXPIRY_MS,
} from "@/lib/topup/constants";
import { TopupError } from "@/lib/topup/errors";
import { createProcessingToken } from "@/lib/topup/reference";
import { ensureTopupSchema } from "@/lib/topup/schema";
import { getTopupSettingsRow } from "@/lib/topup/settings";
import {
  beginTrueMoneyProcessing,
  createTopup,
  getTopupDetail,
  markTopupFailed,
  markTopupPendingRetry,
  markTopupProcessingUnknown,
  recordAttempt,
  requireTopupRow,
} from "@/lib/topup/transactions";
import { creditTrueMoneyTopup } from "@/lib/topup/wallet";
import { enforceCreateRateLimit } from "@/lib/topup/rate-limit";
import { trueMoneyClaim } from "@/lib/topup/providers/truemoney";
import { safeRawExcerpt } from "@/lib/topup/providers/shared";
import type { ProviderResult } from "@/lib/topup/providers/types";
import type { TopupCreateResponse } from "@/lib/topup/types";
import {
  isAmbiguousFailure,
  isConfigFailure,
  isRateLimited,
} from "@/lib/topup/services/provider-outcome";
import {
  assertRequestId,
  parseTrueMoneyVoucher,
  readUserBalance,
  requestFingerprint,
  requireOwnedTopup,
} from "@/lib/topup/services/shared";

export type TopupActor = { id: string; name: string; email: string };

function detailOutcome(
  response: TopupCreateResponse,
  status: string,
): TopupCreateResponse {
  if (status === "completed") response.outcome = "completed";
  else if (status === "processing") response.outcome = "processing";
  else if (status === "pending") response.outcome = "pending";
  else response.outcome = "failed";
  return response;
}

function amountMessage(result: ProviderResult<unknown>): string {
  if (result.kind === "ok") return "รับซองอั่งเปาสำเร็จ";
  return result.message;
}

export async function createTrueMoneyTopup(
  database: Database.Database,
  actor: TopupActor,
  body: { requestId: unknown; link: unknown },
  fetchImpl: typeof fetch = fetch,
): Promise<TopupCreateResponse> {
  ensureTopupSchema(database);
  const settings = getTopupSettingsRow(database);
  if (settings.truemoney_enabled !== 1) {
    throw new TopupError(
      "channel-disabled",
      "ช่องทาง TrueMoney Angpao ยังไม่เปิดให้บริการ",
      409,
    );
  }
  if (!settings.truemoney_phone) {
    throw new TopupError(
      "not-configured",
      "ยังไม่ได้ตั้งค่าเบอร์โทรศัพท์ผู้รับซองอั่งเปา",
      409,
    );
  }
  const requestId = assertRequestId(body.requestId);
  const voucherId = parseTrueMoneyVoucher(body.link);
  enforceCreateRateLimit(database, actor.id);
  const now = Date.now();
  const expiresAt = now + TRUEMONEY_PENDING_EXPIRY_MS;
  const fingerprint = requestFingerprint({
    channel: "truemoney",
    voucher: voucherId.toLowerCase(),
    phone: settings.truemoney_phone,
  });
  const created = createTopup(database, {
    userId: actor.id,
    userName: actor.name,
    userEmail: actor.email,
    channel: "truemoney",
    requestedSatang: null,
    idempotencyKey: requestId,
    fingerprint,
    expiresAt,
    truemoney: {
      phone: settings.truemoney_phone,
      voucherId,
    },
    now,
  });
  if (created.repeated) {
    return detailOutcome(
      {
        topup: getTopupDetail(database, created.row),
        balanceSatang: readUserBalance(database, actor.id),
        repeated: true,
        outcome: "pending",
      },
      created.row.status,
    );
  }

  return runClaim(database, actor, created.row.id, fetchImpl);
}

export async function retryTrueMoneyTopup(
  database: Database.Database,
  actor: TopupActor,
  topupId: string,
  fetchImpl: typeof fetch = fetch,
): Promise<TopupCreateResponse> {
  const row = requireOwnedTopup(database, topupId, actor.id);
  if (row.channel !== "truemoney") {
    throw new TopupError("invalid-request", "รายการนี้ไม่ใช่ TrueMoney", 400);
  }
  if (row.status === "completed") {
    return {
      topup: getTopupDetail(database, row),
      balanceSatang: readUserBalance(database, actor.id),
      repeated: true,
      outcome: "completed",
    };
  }
  if (row.status !== "pending" || row.failure_code !== "RATE_LIMITED") {
    throw new TopupError(
      "pending-review",
      "ต้องรอผู้ดูแลตรวจสอบรายการนี้ ไม่สามารถลองใหม่ได้",
      409,
    );
  }
  enforceCreateRateLimit(database, actor.id);
  return runClaim(database, actor, row.id, fetchImpl);
}

async function runClaim(
  database: Database.Database,
  actor: TopupActor,
  topupId: string,
  fetchImpl: typeof fetch,
): Promise<TopupCreateResponse> {
  const now = Date.now();
  const token = createProcessingToken();
  let row;
  try {
    row = beginTrueMoneyProcessing(database, {
      topupId,
      token,
      now,
    });
  } catch (error) {
    const current = requireTopupRow(database, topupId);
    if (current.status === "completed") {
      return {
        topup: getTopupDetail(database, current),
        balanceSatang: readUserBalance(database, actor.id),
        repeated: true,
        outcome: "completed",
      };
    }
    throw error;
  }

  const result = await trueMoneyClaim(
    {
      phone: row.truemoney_phone ?? "",
      voucherId: row.truemoney_voucher_id ?? "",
    },
    fetchImpl,
  );
  const attemptNo = row.verification_attempts;
  const requestSummary = JSON.stringify({
    provider: "truemoney",
    phoneMasked: row.truemoney_phone
      ? `${row.truemoney_phone.slice(0, 3)}***${row.truemoney_phone.slice(-2)}`
      : null,
    voucherMasked: row.truemoney_voucher_id
      ? `***${row.truemoney_voucher_id.slice(-4)}`
      : null,
  });

  if (result.kind === "ok") {
    recordAttempt(database, {
      topupId,
      attemptNo,
      provider: "truemoney",
      outcome: "verified",
      httpStatus: result.httpStatus,
      errorCode: null,
      errorMessage: null,
      transRef: null,
      amountSatang: result.data.amountSatang,
      durationMs: result.durationMs,
      requestSummary,
      responseExcerpt: safeRawExcerpt(result.raw),
      now,
    });
    try {
      const credited = creditTrueMoneyTopup(database, {
        topupId,
        processingToken: token,
        amountSatang: result.data.amountSatang,
        displayName: result.data.displayName,
        providerRequestId: result.requestId,
        now,
      });
      return {
        topup: getTopupDetail(database, credited.row),
        balanceSatang: credited.balanceSatang,
        repeated: credited.repeated,
        outcome: "completed",
        message: "รับซองอั่งเปาสำเร็จและเพิ่มยอดเงินแล้ว",
      };
    } catch (error) {
      const message =
        error instanceof TopupError
          ? error.message
          : "รับซองอั่งเปาสำเร็จ แต่บันทึกยอดเงินไม่สำเร็จ";
      const failed = markTopupFailed(database, {
        topupId,
        verificationStatus: "error",
        verificationCode: "credit_failed",
        verificationMessage: message,
        failureCode: "credit_failed",
        failureMessage: message,
        providerRequestId: result.requestId,
        now,
      });
      return {
        topup: getTopupDetail(database, failed),
        balanceSatang: readUserBalance(database, actor.id),
        repeated: false,
        outcome: "failed",
        message,
      };
    }
  }

  recordAttempt(database, {
    topupId,
    attemptNo,
    provider: "truemoney",
    outcome: result.kind === "provider-error" ? result.code : result.kind,
    httpStatus: result.httpStatus,
    errorCode: result.code,
    errorMessage: result.message,
    transRef: null,
    amountSatang: null,
    durationMs: result.durationMs,
    retryAfterSeconds: result.retryAfterSeconds,
    requestSummary,
    responseExcerpt: safeRawExcerpt(result.raw),
    now,
  });

  if (isRateLimited(result)) {
    const next = markTopupPendingRetry(database, {
      topupId,
      code: "RATE_LIMITED",
      message: result.message,
      newExpiresAt: now + 10 * 60 * 1_000,
      providerRequestId: result.requestId,
      now,
    });
    return {
      topup: getTopupDetail(database, next),
      balanceSatang: readUserBalance(database, actor.id),
      repeated: false,
      outcome: "pending",
      message: "ระบบรับซองอั่งเปาถูกเรียกถี่เกินไป กรุณากดลองใหม่ภายหลัง",
    };
  }

  if (isAmbiguousFailure(result) || isConfigFailure(result)) {
    const message = isConfigFailure(result)
      ? "ผู้ให้บริการซองอั่งเปายังไม่พร้อมใช้งาน กรุณาติดต่อผู้ดูแล"
      : "ไม่ทราบผลการรับเงินจากซองอั่งเปา ระบบจะไม่เพิ่มยอดจนกว่าจะตรวจสอบได้";
    const processing = markTopupProcessingUnknown(database, {
      topupId,
      code: result.code,
      message,
      providerRequestId: result.requestId,
      retryAfterSeconds: result.retryAfterSeconds,
      now,
    });
    return {
      topup: getTopupDetail(database, processing),
      balanceSatang: readUserBalance(database, actor.id),
      repeated: false,
      outcome: "processing",
      message,
    };
  }

  const failed = markTopupFailed(database, {
    topupId,
    verificationStatus: "rejected",
    verificationCode: result.code,
    verificationMessage: result.message,
    failureCode: result.code,
    failureMessage: result.message,
    providerRequestId: result.requestId,
    now,
  });
  return {
    topup: getTopupDetail(database, failed),
    balanceSatang: readUserBalance(database, actor.id),
    repeated: false,
    outcome: "failed",
    message: amountMessage(result),
  };
}
