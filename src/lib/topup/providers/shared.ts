import { PROVIDER_TIMEOUT_MS } from "@/lib/topup/constants";

export type FetchLike = typeof fetch;

/** Copies a Uint8Array into a plain ArrayBuffer accepted by Blob/Response. */
export function bytesToArrayBuffer(bytes: Uint8Array): ArrayBuffer {
  const copy = new Uint8Array(bytes.byteLength);
  copy.set(bytes);
  return copy.buffer;
}

export function asRecord(value: unknown): Record<string, unknown> | null {
  return value && typeof value === "object" && !Array.isArray(value)
    ? (value as Record<string, unknown>)
    : null;
}

export function readString(
  record: Record<string, unknown> | null,
  key: string,
): string | null {
  const value = record?.[key];
  return typeof value === "string" && value.trim() ? value.trim() : null;
}

export function readNumber(
  record: Record<string, unknown> | null,
  key: string,
): number | null {
  const value = record?.[key];
  return typeof value === "number" && Number.isFinite(value) ? value : null;
}

export function firstString(
  ...values: (string | null | undefined)[]
): string | null {
  for (const value of values) {
    if (typeof value === "string" && value.trim()) return value.trim();
  }
  return null;
}

export function firstNumber(
  ...values: (number | null | undefined)[]
): number | null {
  for (const value of values) {
    if (typeof value === "number" && Number.isFinite(value)) return value;
  }
  return null;
}

export type TimedFetchResult =
  | { ok: true; response: Response; durationMs: number }
  | {
      ok: false;
      kind: "timeout" | "network";
      message: string;
      durationMs: number;
    };

export async function timedFetch(
  fetchImpl: FetchLike,
  url: string,
  init: RequestInit,
  timeoutMs = PROVIDER_TIMEOUT_MS,
): Promise<TimedFetchResult> {
  const startedAt = Date.now();
  try {
    const response = await fetchImpl(url, {
      ...init,
      cache: "no-store",
      signal: AbortSignal.timeout(timeoutMs),
    });
    return { ok: true, response, durationMs: Date.now() - startedAt };
  } catch (error) {
    const name = error instanceof Error ? error.name : "";
    return {
      ok: false,
      kind: name === "TimeoutError" || name === "AbortError" ? "timeout" : "network",
      message:
        name === "TimeoutError" || name === "AbortError"
          ? "ผู้ให้บริการตอบสนองช้าเกินกำหนด"
          : "เชื่อมต่อผู้ให้บริการไม่สำเร็จ",
      durationMs: Date.now() - startedAt,
    };
  }
}

export async function readResponseJson(
  response: Response,
): Promise<unknown | undefined> {
  try {
    return await response.json();
  } catch {
    return undefined;
  }
}

export function retryAfterSeconds(response: Response): number | null {
  const raw = response.headers.get("retry-after");
  if (!raw) return null;
  const value = Number(raw);
  return Number.isFinite(value) && value >= 0 ? Math.min(value, 3600) : null;
}

export function requestIdFrom(response: Response): string | null {
  return (
    response.headers.get("x-request-id") ||
    response.headers.get("x-requestid") ||
    null
  );
}

export function extractProviderMessage(
  body: unknown,
  fallback: string,
): string {
  const record = asRecord(body);
  const message = record ? readString(record, "message") : null;
  return message ?? fallback;
}

/** Trim/redact an arbitrary provider payload before storing it for audit. */
export function safeRawExcerpt(value: unknown, maxLength = 32_000): string | null {
  if (value === undefined) return null;
  try {
    const text = JSON.stringify(value);
    return text.length <= maxLength ? text : text.slice(0, maxLength);
  } catch {
    return null;
  }
}
