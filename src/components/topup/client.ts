"use client";

export class TopupClientError extends Error {
  readonly code: string;
  readonly status: number;
  readonly retryAfterSeconds?: number;

  constructor(
    message: string,
    code: string,
    status: number,
    retryAfterSeconds?: number,
  ) {
    super(message);
    this.name = "TopupClientError";
    this.code = code;
    this.status = status;
    this.retryAfterSeconds = retryAfterSeconds;
  }
}

export async function topupFetch<T>(
  input: string,
  init?: RequestInit,
): Promise<T> {
  const response = await fetch(input, {
    cache: "no-store",
    ...init,
  });
  const payload: unknown = await response.json().catch(() => ({}));
  if (!response.ok) {
    const record =
      payload && typeof payload === "object"
        ? (payload as Record<string, unknown>)
        : {};
    const message =
      typeof record.error === "string" && record.error
        ? record.error
        : "ดำเนินการไม่สำเร็จ กรุณาลองใหม่";
    const code =
      typeof record.code === "string" && record.code
        ? record.code
        : "internal-error";
    const retryAfter = Number(response.headers.get("retry-after"));
    throw new TopupClientError(
      message,
      code,
      response.status,
      Number.isFinite(retryAfter) ? retryAfter : undefined,
    );
  }
  return payload as T;
}

export function newRequestId(): string {
  if (typeof crypto !== "undefined" && "randomUUID" in crypto) {
    return crypto.randomUUID();
  }
  return `xxxxxxxx-xxxx-4xxx-yxxx-xxxxxxxxxxxx`.replace(/[xy]/g, (char) => {
    const random = Math.floor(Math.random() * 16);
    const value = char === "x" ? random : (random & 0x3) | 0x8;
    return value.toString(16);
  });
}

export function clientErrorMessage(error: unknown): string {
  if (error instanceof TopupClientError) return error.message;
  if (error instanceof Error && error.message) return error.message;
  return "ดำเนินการไม่สำเร็จ กรุณาลองใหม่";
}

export async function copyText(value: string): Promise<boolean> {
  try {
    await navigator.clipboard.writeText(value);
    return true;
  } catch {
    return false;
  }
}
