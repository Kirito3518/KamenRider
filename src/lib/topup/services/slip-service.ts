import type Database from "better-sqlite3";
import {
  MAX_VERIFICATION_ATTEMPTS,
  STALE_PROCESSING_MS,
} from "@/lib/topup/constants";
import { TopupError } from "@/lib/topup/errors";
import {
  deleteSlipFile,
  readSlipFile,
  saveSlipFile,
  validateSlipFormData,
} from "@/lib/topup/files";
import {
  getProviderCredentials,
  getTopupSettingsRow,
} from "@/lib/topup/settings";
import { ensureTopupSchema } from "@/lib/topup/schema";
import {
  assertTopupNotExpired,
  beginSlipProcessing,
  beginSlipRetry,
  expireStaleTopups,
  getTopupDetail,
  markTopupFailed,
  markTopupProcessingUnknown,
  recordAttempt,
  recordTopupEvent,
  requireTopupRow,
  type TopupRow,
} from "@/lib/topup/transactions";
import { creditSlipTopup } from "@/lib/topup/wallet";
import {
  enforceSlipAttemptRateLimit,
} from "@/lib/topup/rate-limit";
import { createProcessingToken } from "@/lib/topup/reference";
import { easySlipVerifySlip } from "@/lib/topup/providers/easyslip";
import { slipOkVerifySlip } from "@/lib/topup/providers/slipok";
import { safeRawExcerpt } from "@/lib/topup/providers/shared";
import type {
  NormalizedSlip,
  ProviderResult,
} from "@/lib/topup/providers/types";
import type { TopupSlipOutcome, TopupSlipResponse } from "@/lib/topup/types";
import {
  checkAmount,
  checkRecipient,
  checkSlipDate,
} from "@/lib/topup/verify";
import {
  readUserBalance,
  requireOwnedTopup,
} from "@/lib/topup/services/shared";
import type { TopupActor } from "@/lib/topup/services/truemoney-service";
import {
  isAmbiguousFailure,
  isAmountMismatchFailure,
  isConfigFailure,
  isDuplicateSlipFailure,
  isInvalidSlipFailure,
  isRateLimited,
  isRecipientMismatchFailure,
  isRetryableProviderPending,
  isSlipExpiredFailure,
} from "@/lib/topup/services/provider-outcome";

const SLIP_CHANNELS = new Set(["promptpay", "bank_transfer"]);

function responseFor(
  database: Database.Database,
  topupId: string,
  balanceUserId: string,
  outcome: TopupSlipOutcome,
  message: string,
): TopupSlipResponse {
  const row = requireTopupRow(database, topupId);
  return {
    topup: getTopupDetail(database, row),
    balanceSatang: readUserBalance(database, balanceUserId),
    outcome,
    message,
  };
}

function canRetryRow(row: TopupRow, now = Date.now()): boolean {
  return (
    row.status === "processing" &&
    row.slip_image_path !== null &&
    row.verification_attempts < MAX_VERIFICATION_ATTEMPTS &&
    (row.verification_status === "unknown" ||
      (row.verification_started_at !== null &&
        row.verification_started_at <= now - STALE_PROCESSING_MS))
  );
}

export async function submitSlip(
  database: Database.Database,
  actor: TopupActor,
  topupId: string,
  formData: FormData,
  fetchImpl: typeof fetch = fetch,
): Promise<TopupSlipResponse> {
  ensureTopupSchema(database);
  expireStaleTopups(database);
  const row = requireOwnedTopup(database, topupId, actor.id);
  if (!SLIP_CHANNELS.has(row.channel)) {
    throw new TopupError(
      "invalid-request",
      "รายการนี้ไม่รองรับการอัปโหลดสลิป",
      400,
    );
  }
  if (row.status === "completed") {
    return responseFor(database, row.id, row.user_id, "completed", "รายการนี้เติมเงินสำเร็จแล้ว");
  }
  if (row.status === "failed" || row.status === "expired") {
    throw new TopupError(
      "transaction-finished",
      "รายการนี้สิ้นสุดแล้ว กรุณาเริ่มรายการใหม่",
      409,
    );
  }
  assertTopupNotExpired(database, row);
  const validated = await validateSlipFormData(formData);

  if (row.status === "processing") {
    if (row.slip_image_hash === validated.hash) {
      return responseFor(
        database,
        row.id,
        row.user_id,
        "processing",
        "ระบบกำลังตรวจสอบสลิปนี้อยู่ กรุณารอสักครู่",
      );
    }
    throw new TopupError(
      "verification-in-progress",
      "รายการนี้กำลังตรวจสอบสลิปอยู่ กรุณารอผลก่อน",
      409,
    );
  }
  if (row.status !== "pending") {
    throw new TopupError("transaction-finished", "รายการนี้ไม่พร้อมตรวจสอบ", 409);
  }

  const duplicate = database
    .prepare(
      "SELECT id FROM topup_transactions WHERE slip_image_hash = ? AND id <> ? LIMIT 1",
    )
    .get(validated.hash, row.id) as { id: string } | undefined;
  if (duplicate) {
    markTopupFailed(database, {
      topupId: row.id,
      verificationStatus: "duplicate",
      verificationCode: "duplicate_slip",
      verificationMessage: "รูปสลิปนี้ถูกใช้ในระบบแล้ว",
      failureCode: "duplicate_slip",
      failureMessage: "รูปสลิปนี้ถูกใช้ในระบบแล้ว",
    });
    throw new TopupError("duplicate-slip", "รูปสลิปนี้ถูกใช้ในระบบแล้ว", 409);
  }

  const settings = getTopupSettingsRow(database);
  const credentials = getProviderCredentials(database, settings.slip_provider);
  if (!credentials) {
    throw new TopupError(
      "not-configured",
      "ยังไม่ได้ตั้งค่าผู้ให้บริการตรวจสลิป กรุณาติดต่อผู้ดูแล",
      409,
    );
  }

  const storedName = saveSlipFile(row.id, validated.bytes, validated.mime);
  const token = createProcessingToken();
  let processing: TopupRow;
  try {
    processing = beginSlipProcessing(database, {
      topupId: row.id,
      token,
      path: storedName,
      mime: validated.mime,
      hash: validated.hash,
    });
  } catch (error) {
    deleteSlipFile(storedName);
    if (error instanceof TopupError && error.code === "duplicate-slip") {
      markTopupFailed(database, {
        topupId: row.id,
        verificationStatus: "duplicate",
        verificationCode: "duplicate_slip",
        verificationMessage: "รูปสลิปนี้ถูกใช้ในระบบแล้ว",
        failureCode: "duplicate_slip",
        failureMessage: "รูปสลิปนี้ถูกใช้ในระบบแล้ว",
      });
    }
    throw error;
  }
  recordTopupEvent(database, {
    topupId: row.id,
    fromStatus: "pending",
    toStatus: "processing",
    reason: "อัปโหลดสลิปเพื่อตรวจสอบ",
    actor: "user",
  });
  return executeVerification(database, actor, processing, token, false, fetchImpl);
}

export async function retrySlipVerification(
  database: Database.Database,
  actor: TopupActor,
  topupId: string,
  fetchImpl: typeof fetch = fetch,
): Promise<TopupSlipResponse> {
  ensureTopupSchema(database);
  const row = requireOwnedTopup(database, topupId, actor.id);
  return runRetry(database, actor, row, fetchImpl, "user");
}

export async function adminRetrySlipVerification(
  database: Database.Database,
  actor: TopupActor,
  topupId: string,
  fetchImpl: typeof fetch = fetch,
): Promise<TopupSlipResponse> {
  ensureTopupSchema(database);
  const row = requireTopupRow(database, topupId);
  return runRetry(database, actor, row, fetchImpl, "admin");
}

async function runRetry(
  database: Database.Database,
  actor: TopupActor,
  row: TopupRow,
  fetchImpl: typeof fetch,
  actorKind: "user" | "admin",
): Promise<TopupSlipResponse> {
  if (!SLIP_CHANNELS.has(row.channel) || row.slip_image_path === null) {
    throw new TopupError(
      "invalid-request",
      "รายการนี้ไม่มีสลิปให้ตรวจสอบซ้ำ",
      400,
    );
  }
  if (row.status === "completed") {
    return responseFor(database, row.id, row.user_id, "completed", "รายการนี้เติมเงินสำเร็จแล้ว");
  }
  if (row.status !== "processing") {
    throw new TopupError(
      "transaction-finished",
      "รายการนี้ไม่ได้อยู่ในสถานะรอตรวจสอบซ้ำ",
      409,
    );
  }
  if (row.verification_attempts >= MAX_VERIFICATION_ATTEMPTS) {
    throw new TopupError(
      "too-many-attempts",
      "ตรวจสอบสลิปครบจำนวนครั้งสูงสุดแล้ว กรุณาติดต่อผู้ดูแล",
      409,
    );
  }
  if (!canRetryRow(row)) {
    throw new TopupError(
      "verification-in-progress",
      "ระบบกำลังตรวจสอบสลิปอยู่ กรุณารอผลก่อน",
      409,
    );
  }
  const settings = getTopupSettingsRow(database);
  const credentials = getProviderCredentials(database, settings.slip_provider);
  if (!credentials) {
    throw new TopupError(
      "not-configured",
      "ยังไม่ได้ตั้งค่าผู้ให้บริการตรวจสลิป กรุณาติดต่อผู้ดูแล",
      409,
    );
  }
  enforceSlipAttemptRateLimit(database, row.id);
  const token = createProcessingToken();
  const processing = beginSlipRetry(database, { topupId: row.id, token });
  recordTopupEvent(database, {
    topupId: row.id,
    fromStatus: "processing",
    toStatus: "processing",
    reason: "ลองตรวจสอบสลิปอีกครั้ง",
    actor: actorKind,
  });
  return executeVerification(database, actor, processing, token, true, fetchImpl);
}

async function executeVerification(
  database: Database.Database,
  actor: TopupActor,
  row: TopupRow,
  token: string,
  isRetry: boolean,
  fetchImpl: typeof fetch,
): Promise<TopupSlipResponse> {
  const settings = getTopupSettingsRow(database);
  const provider = settings.slip_provider;
  const credentials = getProviderCredentials(database, provider);
  if (!credentials) {
    const message = "ผู้ให้บริการตรวจสลิปยังไม่พร้อมใช้งาน กรุณาติดต่อผู้ดูแล";
    const next = markTopupProcessingUnknown(database, {
      topupId: row.id,
      code: "not_configured",
      message,
    });
    return {
      topup: getTopupDetail(database, next),
      balanceSatang: readUserBalance(database, row.user_id),
      outcome: "processing",
      message,
    };
  }
  if (!row.slip_image_path || !row.slip_image_mime) {
    const failed = markTopupFailed(database, {
      topupId: row.id,
      verificationStatus: "error",
      verificationCode: "missing_slip",
      verificationMessage: "ไม่พบไฟล์สลิป",
      failureCode: "missing_slip",
      failureMessage: "ไม่พบไฟล์สลิป",
    });
    return {
      topup: getTopupDetail(database, failed),
      balanceSatang: readUserBalance(database, row.user_id),
      outcome: "failed",
      message: "ไม่พบไฟล์สลิป",
    };
  }

  let file: { bytes: Uint8Array; mime: string };
  try {
    const served = readSlipFile(row.slip_image_path, row.slip_image_mime);
    file = { bytes: served.bytes, mime: served.mime };
  } catch {
    const failed = markTopupFailed(database, {
      topupId: row.id,
      verificationStatus: "error",
      verificationCode: "missing_slip",
      verificationMessage: "ไม่พบไฟล์สลิป",
      failureCode: "missing_slip",
      failureMessage: "ไม่พบไฟล์สลิป",
    });
    return {
      topup: getTopupDetail(database, failed),
      balanceSatang: readUserBalance(database, row.user_id),
      outcome: "failed",
      message: "ไม่พบไฟล์สลิป",
    };
  }

  const expectedAmount = row.requested_satang;
  const input = {
    apiKey: credentials.apiKey,
    file: {
      bytes: file.bytes,
      mime: file.mime,
      fileName: `slip-${row.reference}.${file.mime === "image/png" ? "png" : file.mime === "image/webp" ? "webp" : "jpg"}`,
    },
    expectedAmountSatang: expectedAmount ?? 0,
    checkDuplicate: !isRetry,
  };
  const result =
    provider === "easyslip"
      ? await easySlipVerifySlip(input, fetchImpl)
      : await slipOkVerifySlip(
          { ...input, branchId: credentials.branchId ?? "" },
          fetchImpl,
        );

  const now = Date.now();
  const attemptNo = row.verification_attempts;
  const requestSummary = JSON.stringify({
    provider,
    checkDuplicate: !isRetry,
    expectedAmountSatang: expectedAmount,
    fileHash: row.slip_image_hash?.slice(0, 16),
    mime: file.mime,
    source: "server",
  });
  recordAttempt(database, {
    topupId: row.id,
    attemptNo,
    provider,
    outcome: result.kind === "ok" ? "verified" : result.code,
    httpStatus: result.kind === "ok" ? result.httpStatus : result.httpStatus,
    errorCode: result.kind === "ok" ? null : result.code,
    errorMessage: result.kind === "ok" ? null : result.message,
    transRef: result.kind === "ok" ? result.data.transRef : null,
    amountSatang: result.kind === "ok" ? result.data.amountSatang : null,
    durationMs: result.durationMs,
    retryAfterSeconds: result.kind === "ok" ? null : result.retryAfterSeconds,
    requestSummary,
    responseExcerpt: safeRawExcerpt(result.raw),
    now,
  });

  if (result.kind === "ok") {
    return handleVerifiedSlip(database, actor, row, token, result.data, isRetry, now);
  }
  return handleFailedVerification(database, actor, row, result, now);
}

async function handleVerifiedSlip(
  database: Database.Database,
  actor: TopupActor,
  row: TopupRow,
  token: string,
  slip: NormalizedSlip,
  isRetry: boolean,
  now: number,
): Promise<TopupSlipResponse> {
  const amountCheck = checkAmount(row.requested_satang, slip.amountSatang);
  if (!amountCheck.ok) {
    const failed = markTopupFailed(database, {
      topupId: row.id,
      verificationStatus: "rejected",
      verificationCode: "amount_mismatch",
      verificationMessage: amountCheck.message,
      failureCode: "amount_mismatch",
      failureMessage: `${amountCheck.message} (รอรับ ${(row.requested_satang ?? 0) / 100} บาท พบ ${(slip.amountSatang ?? 0) / 100} บาท)`,
      now,
    });
    return {
      topup: getTopupDetail(database, failed),
      balanceSatang: readUserBalance(database, row.user_id),
      outcome: "failed",
      message: amountCheck.message,
    };
  }
  const recipientCheck = checkRecipient(row, slip);
  if (!recipientCheck.ok) {
    const failed = markTopupFailed(database, {
      topupId: row.id,
      verificationStatus: "rejected",
      verificationCode: "recipient_mismatch",
      verificationMessage: recipientCheck.message,
      failureCode: "recipient_mismatch",
      failureMessage: recipientCheck.message,
      now,
    });
    return {
      topup: getTopupDetail(database, failed),
      balanceSatang: readUserBalance(database, row.user_id),
      outcome: "failed",
      message: recipientCheck.message,
    };
  }
  const dateCheck = checkSlipDate(row, slip.dateIso, now);
  if (!dateCheck.ok) {
    const failed = markTopupFailed(database, {
      topupId: row.id,
      verificationStatus: "rejected",
      verificationCode: "slip_date_mismatch",
      verificationMessage: dateCheck.message,
      failureCode: "slip_expired",
      failureMessage: dateCheck.message,
      now,
    });
    return {
      topup: getTopupDetail(database, failed),
      balanceSatang: readUserBalance(database, row.user_id),
      outcome: "failed",
      message: dateCheck.message,
    };
  }
  if (!slip.transRef) {
    const message =
      "ผู้ให้บริการไม่ได้ส่งเลขอ้างอิงสลิป ระบบจะไม่เพิ่มยอดจนกว่าจะตรวจสอบได้";
    const next = markTopupProcessingUnknown(database, {
      topupId: row.id,
      code: "missing_trans_ref",
      message,
      now,
    });
    return {
      topup: getTopupDetail(database, next),
      balanceSatang: readUserBalance(database, row.user_id),
      outcome: "processing",
      message,
    };
  }

  try {
    const credited = creditSlipTopup(database, {
      topupId: row.id,
      processingToken: token,
      provider: slip.provider,
      transRef: slip.transRef,
      amountSatang: slip.amountSatang ?? 0,
      verifiedRecovered: isRetry,
      providerRequestId: null,
      slip: {
        senderName: slip.senderName,
        receiverName: slip.receiverName,
        receiverAccount: slip.receiverAccountNumber,
        bankCode: slip.receiverBankCode,
        dateIso: slip.dateIso,
        code: isRetry ? "verified_recovered" : "verified",
        message: isRetry
          ? "ตรวจสอบสลิปสำเร็จจากการลองใหม่"
          : "ตรวจสอบสลิปผ่าน",
      },
      now,
    });
    return {
      topup: getTopupDetail(database, credited.row),
      balanceSatang: credited.balanceSatang,
      outcome: "completed",
      message: "ตรวจสอบสลิปผ่านและเพิ่มยอดเงินแล้ว",
    };
  } catch (error) {
    if (error instanceof TopupError && error.code === "duplicate-slip") {
      const failed = markTopupFailed(database, {
        topupId: row.id,
        verificationStatus: "duplicate",
        verificationCode: "duplicate_slip",
        verificationMessage: "สลิปนี้ถูกใช้กับรายการอื่นแล้ว",
        failureCode: "duplicate_slip",
        failureMessage: "สลิปนี้ถูกใช้กับรายการอื่นแล้ว",
        now,
      });
      return {
        topup: getTopupDetail(database, failed),
        balanceSatang: readUserBalance(database, row.user_id),
        outcome: "failed",
        message: "สลิปนี้ถูกใช้กับรายการอื่นแล้ว",
      };
    }
    const message =
      error instanceof TopupError
        ? error.message
        : "ตรวจสอบสลิปผ่าน แต่บันทึกยอดเงินไม่สำเร็จ";
    return {
      topup: getTopupDetail(database, requireTopupRow(database, row.id)),
      balanceSatang: readUserBalance(database, row.user_id),
      outcome: "processing",
      message,
    };
  }
}

function handleFailedVerification(
  database: Database.Database,
  actor: TopupActor,
  row: TopupRow,
  result: ProviderResult<NormalizedSlip>,
  now: number,
): TopupSlipResponse {
  const failure = result.kind === "ok" ? null : result;
  if (!failure) throw new TopupError("internal-error", "ผลตรวจสอบไม่ถูกต้อง", 500);

  const markUnknown = (code: string, message: string) => {
    const next = markTopupProcessingUnknown(database, {
      topupId: row.id,
      code,
      message,
      providerRequestId: failure.requestId,
      retryAfterSeconds: failure.retryAfterSeconds,
      now,
    });
    return {
      topup: getTopupDetail(database, next),
      balanceSatang: readUserBalance(database, row.user_id),
      outcome: "processing" as const,
      message,
    };
  };
  const markFailed = (code: string, message: string) => {
    const failed = markTopupFailed(database, {
      topupId: row.id,
      verificationStatus: isDuplicateSlipFailure(result)
        ? "duplicate"
        : isAmountMismatchFailure(result) || isRecipientMismatchFailure(result)
          ? "rejected"
          : "error",
      verificationCode: code,
      verificationMessage: message,
      failureCode: code,
      failureMessage: message,
      now,
    });
    return {
      topup: getTopupDetail(database, failed),
      balanceSatang: readUserBalance(database, row.user_id),
      outcome: "failed" as const,
      message,
    };
  };

  if (isDuplicateSlipFailure(result)) {
    return markFailed("duplicate_slip", "สลิปนี้เคยถูกใช้ตรวจสอบแล้ว");
  }
  if (isAmountMismatchFailure(result)) {
    return markFailed("amount_mismatch", "ยอดเงินในสลิปไม่ตรงกับรายการ");
  }
  if (isRecipientMismatchFailure(result)) {
    return markFailed("recipient_mismatch", "บัญชีผู้รับในสลิปไม่ตรงกับรายการ");
  }
  if (isSlipExpiredFailure(result)) {
    return markFailed("slip_expired", "สลิปหมดอายุหรือไม่พบรายการ");
  }
  if (isInvalidSlipFailure(result)) {
    return markFailed("invalid_slip", failure.message);
  }
  if (isRateLimited(result)) {
    return markUnknown(
      "rate_limited",
      failure.message || "ผู้ให้บริการตรวจสลิปถูกเรียกถี่เกินไป กรุณาลองใหม่ภายหลัง",
    );
  }
  if (isRetryableProviderPending(result)) {
    return markUnknown("provider_pending", failure.message);
  }
  if (isConfigFailure(result)) {
    return markUnknown(
      failure.code,
      "ผู้ให้บริการตรวจสลิปยังไม่พร้อมใช้งาน กรุณาติดต่อผู้ดูแล",
    );
  }
  if (isAmbiguousFailure(result)) {
    return markUnknown(
      failure.code,
      "ยังไม่ทราบผลการตรวจสอบสลิป ระบบจะไม่เพิ่มยอดจนกว่าจะยืนยันได้",
    );
  }
  return markUnknown(
    failure.code,
    failure.message || "ตรวจสอบสลิปไม่สำเร็จ กรุณาติดต่อผู้ดูแล",
  );
}
