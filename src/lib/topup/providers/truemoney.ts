/**
 * TrueMoney Angpao gateway client (third-party service).
 * https://tmn-angpao.pages.dev/
 *
 * The API claims an angpao link with a receiver phone. A network timeout is
 * ambiguous (the claim may have happened), so the service never auto-retries
 * this call; only an explicit rate-limit response is safe to retry.
 */

import { PROVIDER_TIMEOUT_MS } from "@/lib/topup/constants";
import { parseProviderAmountToSatang } from "@/lib/topup/amount";
import type {
  ProviderResult,
  TrueMoneyClaimData,
} from "@/lib/topup/providers/types";
import {
  asRecord,
  readResponseJson,
  readString,
  requestIdFrom,
  retryAfterSeconds,
  timedFetch,
  type FetchLike,
} from "@/lib/topup/providers/shared";

function apiUrl() {
  return (
    process.env.TRUEMONEY_ANGPAO_API_URL || "https://tmn-angpao.pages.dev/api"
  ).trim();
}

function messageForCode(code: string | null, fallback: string | null) {
  switch (code) {
    case "VOUCHER_EXPIRED":
      return "ลิงก์ซองอั่งเปาหมดอายุแล้ว";
    case "VOUCHER_ALREADY_USED":
      return "ลิงก์ซองอั่งเปานี้ถูกใช้แล้ว";
    case "SELF_CLAIM_NOT_ALLOWED":
      return "ไม่สามารถใช้ซองอั่งเปาของบัญชีตัวเองได้";
    case "INVALID_PHONE_OR_USED":
      return "เบอร์รับเงินไม่ถูกต้อง หรือเคยรับเงินซองนี้แล้ว";
    case "INVALID_PHONE":
      return "เบอร์รับเงินไม่ถูกต้อง (ต้องเป็น 10 หลักและขึ้นต้นด้วย 0)";
    case "INVALID_VOUCHER":
      return "กรุณาระบุลิงก์ซองอั่งเปาให้ถูกต้อง";
    case "MISSING_PARAMETER":
      return "ข้อมูลซองอั่งเปาไม่ครบถ้วน";
    case "INVALID_PATH":
      return "รูปแบบคำขอไม่ถูกต้อง";
    case "INVALID_JSON":
      return "ข้อมูลคำขอไม่ถูกต้อง";
    case "VOUCHER_NOT_FOUND":
      return "ไม่พบลิงก์ซองอั่งเปานี้";
    case "RATE_LIMITED":
      return "ระบบรับซองอั่งเปาถูกเรียกถี่เกินไป กรุณารอสักครู่";
    case "EXTERNAL_SERVER_ERROR":
    case "EXTERNAL_INVALID_RESPONSE":
      return "ผู้ให้บริการซองอั่งเปาขัดข้องชั่วคราว กรุณาลองใหม่ภายหลัง";
    case "INTERNAL_SERVER_ERROR":
      return "ผู้ให้บริการซองอั่งเปาขัดข้องชั่วคราว";
    default:
      return fallback ?? "รับเงินจากซองอั่งเปาไม่สำเร็จ";
  }
}

function failure(
  code: string,
  message: string,
  options: {
    httpStatus: number | null;
    durationMs: number;
    requestId: string | null;
    retryAfterSeconds?: number | null;
    retryable?: boolean;
    raw: unknown;
    kind?: "provider-error" | "invalid-response" | "timeout" | "network";
  },
): ProviderResult<never> {
  return {
    kind: options.kind ?? "provider-error",
    code,
    message,
    httpStatus: options.httpStatus,
    durationMs: options.durationMs,
    requestId: options.requestId,
    retryAfterSeconds: options.retryAfterSeconds ?? null,
    retryable: options.retryable ?? false,
    raw: options.raw,
  };
}

export async function trueMoneyClaim(
  input: { phone: string; voucherId: string },
  fetchImpl: FetchLike = fetch,
): Promise<ProviderResult<TrueMoneyClaimData>> {
  const result = await timedFetch(
    fetchImpl,
    apiUrl(),
    {
      method: "POST",
      headers: {
        "Content-Type": "application/json",
        Accept: "application/json",
      },
      body: JSON.stringify({
        phone: input.phone,
        voucher_id: input.voucherId,
      }),
    },
    PROVIDER_TIMEOUT_MS,
  );
  if (!result.ok) {
    return failure(
      result.kind === "timeout" ? "timeout" : "network",
      result.message,
      {
        httpStatus: null,
        durationMs: result.durationMs,
        requestId: null,
        // Ambiguous: the claim may have happened. Never auto-retry.
        retryable: false,
        raw: null,
        kind: result.kind,
      },
    );
  }
  const response = result.response;
  const requestId = requestIdFrom(response);
  const retryAfter = retryAfterSeconds(response);
  const payload = await readResponseJson(response);
  const record = asRecord(payload);
  const code = record ? readString(record, "code") : null;
  const bodyMessage = record ? readString(record, "message") : null;
  const success = record?.success === true;

  if (!response.ok || !success) {
    const message = messageForCode(code, bodyMessage);
    return failure(code ?? `http_${response.status}`, message, {
      httpStatus: response.status,
      durationMs: result.durationMs,
      requestId,
      retryAfterSeconds: retryAfter,
      retryable: response.status === 429 || code === "RATE_LIMITED",
      raw: payload,
    });
  }

  const data = asRecord(record?.data);
  const rawAmount = data?.amount;
  const amountSatang = parseProviderAmountToSatang(rawAmount);
  if (!data || amountSatang === null) {
    // The claim reported success but we cannot determine the amount. The
    // service must not credit and must keep the transaction for review.
    return failure(
      "invalid_response",
      "รับซองอั่งเปาสำเร็จ แต่ไม่พบยอดเงินที่ได้รับ",
      {
        httpStatus: response.status,
        durationMs: result.durationMs,
        requestId,
        retryAfterSeconds: retryAfter,
        retryable: false,
        raw: payload,
        kind: "invalid-response",
      },
    );
  }

  return {
    kind: "ok",
    httpStatus: response.status,
    durationMs: result.durationMs,
    requestId,
    raw: payload,
    data: {
      voucherId: input.voucherId,
      amountSatang,
      displayName: readString(data, "name"),
      requestId,
    },
  };
}
