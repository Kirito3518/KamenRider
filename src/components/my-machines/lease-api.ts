"use client";

/** Error carrying the HTTP status so dialogs can give action-specific feedback. */
export class LeaseApiError extends Error {
  constructor(
    message: string,
    readonly status: number,
  ) {
    super(message);
    this.name = "LeaseApiError";
  }
}

const statusMessages: Record<number, string> = {
  401: "กรุณาเข้าสู่ระบบ",
  403: "ไม่มีสิทธิ์ทำรายการนี้",
  404: "ไม่พบรายการเช่านี้",
  409: "รายการขัดแย้งกับสถานะล่าสุด กรุณารีเฟรชแล้วลองใหม่",
  503: "ระบบเช่ายังไม่ตอบสนอง กรุณาลองใหม่",
};

const genericServerErrors = new Set([
  "Operation rejected",
  "Controller unavailable",
  "Invalid request",
  "Authentication required",
]);

function serverMessage(data: unknown) {
  if (!data || typeof data !== "object" || Array.isArray(data)) return null;
  const value = (data as Record<string, unknown>).error;
  return typeof value === "string" && value.trim() ? value.trim() : null;
}

/**
 * Shared JSON client for the renter's lease routes. Only the owner's own routes
 * are called from here; no bearer token is ever read or stored.
 */
export async function leaseApi<T>(path: string, body?: unknown): Promise<T> {
  let response: Response;
  try {
    response = await fetch(path, {
      method: body === undefined ? "GET" : "POST",
      cache: "no-store",
      headers:
        body === undefined ? undefined : { "Content-Type": "application/json" },
      body: body === undefined ? undefined : JSON.stringify(body),
    });
  } catch {
    throw new LeaseApiError("เชื่อมต่อระบบไม่ได้ กรุณาลองใหม่", 0);
  }

  const data: unknown = await response.json().catch(() => null);
  if (!response.ok) {
    const message = serverMessage(data);
    throw new LeaseApiError(
      message && !genericServerErrors.has(message)
        ? message
        : (statusMessages[response.status] ?? `${response.status}: คำขอไม่สำเร็จ`),
      response.status,
    );
  }
  return data as T;
}

export function leaseErrorMessage(error: unknown) {
  return error instanceof Error
    ? error.message
    : "ทำรายการไม่สำเร็จ กรุณาลองใหม่";
}

export function leaseErrorStatus(error: unknown) {
  return error instanceof LeaseApiError ? error.status : 0;
}

export function requestRentalPower(rentalId: string) {
  return leaseApi<{ canRequestPower?: boolean; wakeRequestedAtUtc?: string | null }>(
    `/api/my-leases/${encodeURIComponent(rentalId)}/power`,
    { requestId: crypto.randomUUID() },
  );
}

export function cancelRental(rentalId: string, expectedRevision: number) {
  return leaseApi<{ lease: unknown; refundedSatang: number; paymentRefundedSatang: number }>(
    `/api/my-leases/${encodeURIComponent(rentalId)}/cancel`,
    { expectedRevision, idempotencyKey: crypto.randomUUID() },
  );
}
