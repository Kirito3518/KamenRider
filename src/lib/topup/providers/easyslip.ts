/**
 * EasySlip API v1 client (server-only).
 * https://document.easyslip.com/th/v1/qr/generate
 * https://document.easyslip.com/th/v1/verify/bank/image
 * https://document.easyslip.com/th/v1/me
 *
 * This is the only provider allowed to generate PromptPay QR codes. Slip
 * verification also uses it only when the system settings select EasySlip.
 */

import { PROVIDER_TIMEOUT_MS } from "@/lib/topup/constants";
import { parseProviderAmountToSatang, satangToBahtNumber } from "@/lib/topup/amount";
import type {
  EasySlipQrData,
  EasySlipQrInput,
  NormalizedSlip,
  ProviderResult,
  ProviderTestData,
  VerifySlipInput,
} from "@/lib/topup/providers/types";
import {
  asRecord,
  bytesToArrayBuffer,
  extractProviderMessage,
  firstString,
  readResponseJson,
  readString,
  requestIdFrom,
  retryAfterSeconds,
  timedFetch,
  type FetchLike,
} from "@/lib/topup/providers/shared";

function baseUrl() {
  return (process.env.EASYSLIP_API_URL || "https://api.easyslip.com").replace(
    /\/+$/,
    "",
  );
}

function authHeaders(apiKey: string) {
  return {
    Authorization: `Bearer ${apiKey}`,
    Accept: "application/json",
  };
}

function isRetryable(status: number, message: string) {
  if (status === 429 || status >= 500) return true;
  return message === "slip_pending" || message === "rate_limit_exceeded";
}

function failure(
  code: string,
  message: string,
  options: {
    httpStatus: number | null;
    durationMs: number;
    requestId: string | null;
    retryAfterSeconds: number | null;
    retryable: boolean;
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
    retryAfterSeconds: options.retryAfterSeconds,
    retryable: options.retryable,
    raw: options.raw,
  };
}

function normalizeSlip(data: Record<string, unknown>): NormalizedSlip {
  const receiver = asRecord(data.receiver);
  const receiverAccount = asRecord(receiver?.account);
  const receiverBank =
    asRecord(receiver?.bank) ?? asRecord(receiverAccount?.bank);
  const receiverProxy =
    asRecord(receiverAccount?.proxy) ?? asRecord(receiver?.proxy);
  const sender = asRecord(data.sender);
  const senderAccount = asRecord(sender?.account);
  const receiverNameRecord = asRecord(receiverAccount?.name);
  const senderNameRecord = asRecord(senderAccount?.name);
  const amountRecord = asRecord(data.amount);
  const rawAmount = amountRecord ? amountRecord.amount : data.amount;

  return {
    provider: "easyslip",
    transRef: readString(data, "transRef"),
    amountSatang: parseProviderAmountToSatang(rawAmount),
    dateIso: readString(data, "date"),
    senderName: firstString(
      receiverNameRecord ? null : null,
      readString(senderNameRecord, "th"),
      readString(senderNameRecord, "en"),
      readString(senderAccount, "displayName"),
    ),
    receiverName: firstString(
      readString(receiverNameRecord, "th"),
      readString(receiverNameRecord, "en"),
      readString(receiverAccount, "displayName"),
    ),
    receiverBankCode: firstString(
      readString(receiverBank, "id"),
      readString(receiverBank, "code"),
    ),
    receiverBankName: firstString(
      readString(receiverBank, "name"),
      readString(receiverBank, "short"),
    ),
    receiverAccountNumber: firstString(
      readString(receiverAccount, "value"),
      receiverAccount?.bank ? readString(asRecord(receiverAccount.bank), "account") : null,
    ),
    receiverProxyType: firstString(
      readString(receiverProxy, "type"),
      readString(receiverProxy, "proxyType"),
    ),
    receiverProxyValue: firstString(
      readString(receiverProxy, "account"),
      readString(receiverProxy, "value"),
    ),
  };
}

export async function easySlipGenerateQr(
  input: EasySlipQrInput,
  fetchImpl: FetchLike = fetch,
): Promise<ProviderResult<EasySlipQrData>> {
  const body: Record<string, unknown> = {
    type: "PROMPTPAY",
    amount: satangToBahtNumber(input.amountSatang),
  };
  body[input.promptpayType] = input.promptpayNumber;

  const result = await timedFetch(
    fetchImpl,
    `${baseUrl()}/v1/qr/generate`,
    {
      method: "POST",
      headers: {
        ...authHeaders(input.apiKey),
        "Content-Type": "application/json",
      },
      body: JSON.stringify(body),
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
        retryAfterSeconds: null,
        retryable: true,
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
  const message = record ? readString(record, "message") : null;
  const status = record ? record.status : null;

  if (
    !response.ok ||
    status !== 200 ||
    record?.data === undefined
  ) {
    const code =
      message ??
      (typeof status === "number" ? `easyslip_${status}` : `http_${response.status}`);
    return failure(code, message ?? "สร้าง QR PromptPay ไม่สำเร็จ", {
      httpStatus: response.status,
      durationMs: result.durationMs,
      requestId,
      retryAfterSeconds: retryAfter,
      retryable: isRetryable(response.status, code),
      raw: payload,
    });
  }
  const data = asRecord(record.data);
  const image = data ? readString(data, "image") : null;
  const mime = data ? readString(data, "mime") : null;
  const payloadText = data ? readString(data, "payload") : null;
  if (!image || mime !== "image/png" || image.length > 2_000_000) {
    return failure("invalid_response", "ผู้ให้บริการ QR ตอบกลับข้อมูลไม่ถูกต้อง", {
      httpStatus: response.status,
      durationMs: result.durationMs,
      requestId,
      retryAfterSeconds: retryAfter,
      retryable: true,
      raw: payload,
      kind: "invalid-response",
    });
  }
  return {
    kind: "ok",
    httpStatus: response.status,
    durationMs: result.durationMs,
    requestId,
    raw: payload,
    data: {
      imageDataUrl: `data:${mime};base64,${image}`,
      payload: payloadText ?? "",
      mime,
    },
  };
}

export async function easySlipVerifySlip(
  input: VerifySlipInput,
  fetchImpl: FetchLike = fetch,
): Promise<ProviderResult<NormalizedSlip>> {
  const form = new FormData();
  form.append(
    "file",
    new Blob([bytesToArrayBuffer(input.file.bytes)], { type: input.file.mime }),
    input.file.fileName,
  );
  form.append("checkDuplicate", input.checkDuplicate ? "true" : "false");

  const result = await timedFetch(
    fetchImpl,
    `${baseUrl()}/v1/verify`,
    {
      method: "POST",
      headers: authHeaders(input.apiKey),
      body: form,
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
        retryAfterSeconds: null,
        retryable: true,
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
  const message = record ? readString(record, "message") : null;
  const bodyStatus = record ? record.status : null;

  if (!response.ok || bodyStatus !== 200 || record?.data === undefined) {
    const code =
      message ??
      (typeof bodyStatus === "number"
        ? `easyslip_${bodyStatus}`
        : `http_${response.status}`);
    return failure(code, message ?? "ตรวจสอบสลิปไม่สำเร็จ", {
      httpStatus: response.status,
      durationMs: result.durationMs,
      requestId,
      retryAfterSeconds: retryAfter,
      retryable: isRetryable(response.status, code),
      raw: payload,
    });
  }

  const data = asRecord(record.data);
  if (!data) {
    return failure("invalid_response", "ผู้ให้บริการตรวจสลิปตอบกลับข้อมูลไม่ถูกต้อง", {
      httpStatus: response.status,
      durationMs: result.durationMs,
      requestId,
      retryAfterSeconds: retryAfter,
      retryable: true,
      raw: payload,
      kind: "invalid-response",
    });
  }
  return {
    kind: "ok",
    httpStatus: response.status,
    durationMs: result.durationMs,
    requestId,
    raw: payload,
    data: normalizeSlip(data),
  };
}

export async function easySlipTestConnection(
  apiKey: string,
  fetchImpl: FetchLike = fetch,
): Promise<ProviderResult<ProviderTestData>> {
  const result = await timedFetch(
    fetchImpl,
    `${baseUrl()}/v1/me`,
    { method: "GET", headers: authHeaders(apiKey) },
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
        retryAfterSeconds: null,
        retryable: true,
        raw: null,
        kind: result.kind,
      },
    );
  }
  const response = result.response;
  const requestId = requestIdFrom(response);
  const payload = await readResponseJson(response);
  const record = asRecord(payload);
  const message = record ? readString(record, "message") : null;
  if (!response.ok || record?.status !== 200 || record.data === undefined) {
    return failure(
      message ?? `easyslip_${response.status}`,
      message ?? "EasySlip ปฏิเสธการเชื่อมต่อ",
      {
        httpStatus: response.status,
        durationMs: result.durationMs,
        requestId,
        retryAfterSeconds: retryAfterSeconds(response),
        retryable: response.status === 429 || response.status >= 500,
        raw: payload,
      },
    );
  }
  const data = asRecord(record.data);
  return {
    kind: "ok",
    httpStatus: response.status,
    durationMs: result.durationMs,
    requestId,
    raw: payload,
    data: {
      application: readString(data, "application"),
      remainingQuota: asNumber(data, "remainingQuota"),
      usedQuota: asNumber(data, "usedQuota"),
      maxQuota: asNumber(data, "maxQuota"),
      expiredAt: readString(data, "expiredAt"),
    },
  };
}

function asNumber(
  record: Record<string, unknown> | null,
  key: string,
): number | null {
  const value = record?.[key];
  return typeof value === "number" && Number.isFinite(value) ? value : null;
}

export { extractProviderMessage };
