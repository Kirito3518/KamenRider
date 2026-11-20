/**
 * SlipOk API v1.8 client (server-only).
 * https://slipok.com/api-documentation/check-slip/
 * https://slipok.com/api-documentation/check-slip-quota/
 *
 * `log:false` is intentional: it avoids binding verification to a LIFF receiver
 * account and keeps the local duplicate/amount/recipient guards authoritative.
 * The API key travels only in the server-to-server `x-authorization` header.
 */

import { PROVIDER_TIMEOUT_MS } from "@/lib/topup/constants";
import { parseProviderAmountToSatang, satangToBahtNumber } from "@/lib/topup/amount";
import type {
  NormalizedSlip,
  ProviderResult,
  ProviderTestData,
  SlipOkVerifyInput,
} from "@/lib/topup/providers/types";
import {
  asRecord,
  bytesToArrayBuffer,
  firstString,
  firstNumber,
  readResponseJson,
  readString,
  requestIdFrom,
  retryAfterSeconds,
  timedFetch,
  type FetchLike,
} from "@/lib/topup/providers/shared";

function baseUrl() {
  return (process.env.SLIPOK_API_URL || "https://api.slipok.com").replace(
    /\/+$/,
    "",
  );
}

function endpoint(branchId: string) {
  return `${baseUrl()}/api/line/apikey/${encodeURIComponent(branchId)}`;
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

function errorCodeFrom(body: unknown): string | null {
  const record = asRecord(body);
  const code = record?.code;
  if (typeof code === "number" && Number.isInteger(code)) return String(code);
  if (typeof code === "string" && code.trim()) return code.trim();
  return null;
}

function messageForCode(code: string | null, fallback: string | null): string {
  switch (code) {
    case "1000":
      return "ข้อมูล QR Code ไม่ครบถ้วน";
    case "1001":
      return "ไม่พบข้อมูลสาขา กรุณาตรวจสอบ Branch ID";
    case "1002":
      return "API Key ของ SlipOk ไม่ถูกต้อง";
    case "1003":
      return "แพ็กเกจ SlipOk หมดอายุแล้ว";
    case "1004":
      return "แพ็กเกจ SlipOk ใช้เกินโควต้าแล้ว";
    case "1005":
      return "ไฟล์ไม่ใช่ไฟล์ภาพที่รองรับ";
    case "1006":
      return "รูปภาพไม่ถูกต้อง";
    case "1007":
      return "ไม่พบ QR Code ในรูปสลิป";
    case "1008":
      return "QR Code ไม่ใช่ QR สำหรับตรวจสอบการชำระเงิน";
    case "1009":
      return "ธนาคารขัดข้องชั่วคราว กรุณาลองใหม่ภายหลัง";
    case "1010":
      return "สลิปจากธนาคารนี้ต้องรอตรวจสอบภายหลัง";
    case "1011":
      return "QR Code หมดอายุ หรือไม่มีรายการอยู่จริง";
    case "1012":
      return "สลิปนี้เคยถูกใช้ตรวจสอบแล้ว";
    case "1013":
      return "ยอดเงินในสลิปไม่ตรงกับรายการ";
    case "1014":
      return "บัญชีผู้รับในสลิปไม่ตรงกับบัญชีร้านค้า";
    default:
      return fallback ?? "SlipOk ตรวจสอบสลิปไม่สำเร็จ";
  }
}

function retryableCode(code: string | null) {
  return code === "1009" || code === "1010";
}

function normalizeSlip(data: Record<string, unknown>): NormalizedSlip {
  const receiver = asRecord(data.receiver);
  const receiverAccount = asRecord(receiver?.account);
  const receiverProxy = asRecord(receiver?.proxy);
  const sender = asRecord(data.sender);
  const receiverName = firstString(
    readString(receiver, "displayName"),
    readString(receiver, "name"),
  );
  const senderName = firstString(
    readString(sender, "displayName"),
    readString(sender, "name"),
  );

  const timestamp = readString(data, "transTimestamp");
  const dateText = readString(data, "transDate");
  const timeText = readString(data, "transTime");
  let dateIso: string | null = null;
  if (timestamp && Number.isFinite(Date.parse(timestamp))) {
    dateIso = new Date(timestamp).toISOString();
  } else if (dateText && timeText && /^\d{8}$/.test(dateText)) {
    const yyyy = dateText.slice(0, 4);
    const mm = dateText.slice(4, 6);
    const dd = dateText.slice(6, 8);
    const parsed = Date.parse(`${yyyy}-${mm}-${dd}T${timeText}+07:00`);
    if (Number.isFinite(parsed)) dateIso = new Date(parsed).toISOString();
  }

  const amount = parseProviderAmountToSatang(
    firstNumber(
      typeof data.amount === "number" ? data.amount : null,
      typeof data.amount === "string" ? Number(data.amount) : null,
    ),
  );

  return {
    provider: "slipok",
    transRef: readString(data, "transRef"),
    amountSatang: amount,
    dateIso,
    senderName,
    receiverName,
    receiverBankCode: firstString(
      readString(data, "receivingBank"),
      readString(receiverAccount, "bankCode"),
    ),
    receiverBankName: firstString(
      readString(receiverAccount, "bankName"),
      readString(receiver, "bankName"),
    ),
    receiverAccountNumber: firstString(
      readString(receiverAccount, "value"),
      readString(receiverAccount, "account"),
    ),
    receiverProxyType: firstString(
      readString(receiverProxy, "type"),
      readString(receiver, "proxyType"),
    ),
    receiverProxyValue: firstString(
      readString(receiverProxy, "value"),
      readString(receiverProxy, "account"),
    ),
  };
}

export async function slipOkVerifySlip(
  input: SlipOkVerifyInput,
  fetchImpl: FetchLike = fetch,
): Promise<ProviderResult<NormalizedSlip>> {
  const form = new FormData();
  form.append(
    "files",
    new Blob([bytesToArrayBuffer(input.file.bytes)], { type: input.file.mime }),
    input.file.fileName,
  );
  form.append("amount", String(satangToBahtNumber(input.expectedAmountSatang)));
  form.append("log", "false");

  const result = await timedFetch(
    fetchImpl,
    endpoint(input.branchId),
    {
      method: "POST",
      headers: { "x-authorization": input.apiKey, Accept: "application/json" },
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
  const code = errorCodeFrom(payload);
  const bodyMessage = record ? readString(record, "message") : null;

  if (!response.ok || record?.success !== true) {
    const message = messageForCode(code, bodyMessage);
    return failure(code ?? `http_${response.status}`, message, {
      httpStatus: response.status,
      durationMs: result.durationMs,
      requestId,
      retryAfterSeconds: retryAfter,
      retryable: retryableCode(code) || response.status === 429 || response.status >= 500,
      raw: payload,
    });
  }

  const data = asRecord(record.data);
  if (!data) {
    return failure("invalid_response", "SlipOk ตอบกลับข้อมูลไม่ถูกต้อง", {
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

export async function slipOkTestConnection(
  apiKey: string,
  branchId: string,
  fetchImpl: FetchLike = fetch,
): Promise<ProviderResult<ProviderTestData>> {
  const result = await timedFetch(
    fetchImpl,
    `${endpoint(branchId)}/quota`,
    {
      method: "GET",
      headers: { "x-authorization": apiKey, Accept: "application/json" },
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
  const code = errorCodeFrom(payload);
  const bodyMessage = record ? readString(record, "message") : null;
  if (!response.ok || record?.success !== true) {
    return failure(code ?? `http_${response.status}`, messageForCode(code, bodyMessage), {
      httpStatus: response.status,
      durationMs: result.durationMs,
      requestId,
      retryAfterSeconds: retryAfterSeconds(response),
      retryable: response.status === 429 || response.status >= 500,
      raw: payload,
    });
  }
  const data = asRecord(record.data);
  return {
    kind: "ok",
    httpStatus: response.status,
    durationMs: result.durationMs,
    requestId,
    raw: payload,
    data: {
      quota: asNullableNumber(data, "quota"),
      overQuota: asNullableNumber(data, "overQuota"),
    },
  };
}

function asNullableNumber(
  record: Record<string, unknown> | null,
  key: string,
): number | null {
  const value = record?.[key];
  return typeof value === "number" && Number.isFinite(value) ? value : null;
}
