import type Database from "better-sqlite3";
import { PROMPTPAY_QR_EXPIRY_MS } from "@/lib/topup/constants";
import { TopupError } from "@/lib/topup/errors";
import { ensureTopupSchema } from "@/lib/topup/schema";
import { getProviderCredentials, getTopupSettingsRow } from "@/lib/topup/settings";
import {
  createTopup,
  getTopupDetail,
  markTopupFailed,
  requireTopupRow,
  updateTopupQr,
} from "@/lib/topup/transactions";
import { enforceCreateRateLimit } from "@/lib/topup/rate-limit";
import { easySlipGenerateQr } from "@/lib/topup/providers/easyslip";
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

function resultFor(
  database: Database.Database,
  topupId: string,
  actorId: string,
  outcome: TopupCreateOutcome,
  repeated: boolean,
  message?: string,
): TopupCreateResponse {
  const row = requireTopupRow(database, topupId);
  return {
    topup: getTopupDetail(database, row),
    balanceSatang: readUserBalance(database, actorId),
    repeated,
    outcome,
    message,
  };
}

export async function createPromptpayTopup(
  database: Database.Database,
  actor: TopupActor,
  body: { requestId: unknown; amountSatang: unknown },
  fetchImpl: typeof fetch = fetch,
): Promise<TopupCreateResponse> {
  ensureTopupSchema(database);
  const settings = getTopupSettingsRow(database);
  if (settings.promptpay_enabled !== 1) {
    throw new TopupError(
      "channel-disabled",
      "ช่องทาง PromptPay QR ยังไม่เปิดให้บริการ",
      409,
    );
  }
  if (
    !settings.promptpay_number ||
    !settings.promptpay_account_name ||
    !settings.promptpay_type
  ) {
    throw new TopupError(
      "not-configured",
      "ยังไม่ได้ตั้งค่าบัญชี PromptPay สำหรับรับเงิน",
      409,
    );
  }
  const credentials = getProviderCredentials(database, "easyslip");
  if (!credentials) {
    throw new TopupError(
      "not-configured",
      "ยังไม่ได้ตั้งค่า EasySlip API Key สำหรับสร้าง QR",
      409,
    );
  }
  const requestId = assertRequestId(body.requestId);
  const amountSatang = assertTopupAmount(body.amountSatang);
  enforceCreateRateLimit(database, actor.id);
  const now = Date.now();
  const expiresAt = now + PROMPTPAY_QR_EXPIRY_MS;
  const fingerprint = requestFingerprint({
    channel: "promptpay",
    amountSatang,
    type: settings.promptpay_type,
    number: settings.promptpay_number,
    name: settings.promptpay_account_name,
  });
  const created = createTopup(database, {
    userId: actor.id,
    userName: actor.name,
    userEmail: actor.email,
    channel: "promptpay",
    requestedSatang: amountSatang,
    idempotencyKey: requestId,
    fingerprint,
    expiresAt,
    promptpay: {
      type: settings.promptpay_type,
      number: settings.promptpay_number,
      accountName: settings.promptpay_account_name,
    },
    now,
  });

  const row = created.row;
  if (created.repeated) {
    if (row.status === "pending" && row.qr_image_data_url) {
      return resultFor(database, row.id, actor.id, "pending", true);
    }
    if (row.status === "completed") {
      return resultFor(database, row.id, actor.id, "completed", true);
    }
    if (row.status === "failed" || row.status === "expired") {
      throw new TopupError(
        "transaction-finished",
        "รายการนี้สิ้นสุดแล้ว กรุณาเริ่มรายการใหม่",
        409,
      );
    }
    if (row.status === "processing") {
      return resultFor(database, row.id, actor.id, "processing", true);
    }
    // pending without a QR: a previous attempt stopped before the provider call.
  }

  const qr = await easySlipGenerateQr(
    {
      apiKey: credentials.apiKey,
      promptpayType: settings.promptpay_type,
      promptpayNumber: settings.promptpay_number,
      amountSatang,
    },
    fetchImpl,
  );

  if (qr.kind !== "ok") {
    const message = qr.message || "สร้าง QR PromptPay ไม่สำเร็จ";
    const failed = markTopupFailed(database, {
      topupId: row.id,
      verificationStatus: "error",
      verificationCode: qr.code,
      verificationMessage: message,
      failureCode: qr.code,
      failureMessage: message,
      providerRequestId: qr.requestId,
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

  const updated = updateTopupQr(database, row.id, {
    qrPayload: qr.data.payload,
    qrImageDataUrl: qr.data.imageDataUrl,
    qrExpiresAt: expiresAt,
    expiresAt,
    now,
  });
  return {
    topup: getTopupDetail(database, updated),
    balanceSatang: readUserBalance(database, actor.id),
    repeated: created.repeated,
    outcome: "pending",
    message: "สร้าง QR PromptPay แล้ว กรุณาชำระเงินและอัปโหลดสลิป",
  };
}
