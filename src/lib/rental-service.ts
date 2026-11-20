import type Database from "better-sqlite3";
import { getAuth } from "@/lib/auth";
import { ControllerError } from "@/lib/controller-client";
import type { RentalPayment } from "@/lib/rental-payments";

/** Server-only helpers shared by the customer rental routes. */

export type ControllerCall = <T>(path: string, init?: RequestInit) => Promise<T>;

export type ControllerLeaseRecord = {
  id: string;
  userId: string;
  machineId: string;
  state: string;
  revision: number;
  startsAtUtc: string | null;
  expiresAtUtc: string | null;
  pendingRevision: number | null;
  endedAtUtc?: string | null;
};

export async function authDatabase() {
  const auth = await getAuth();
  return auth.options.database as unknown as Database.Database;
}

export type LeaseCreateOutcome =
  | { kind: "created"; lease: ControllerLeaseRecord }
  | { kind: "rejected"; reason: string }
  | { kind: "unknown" };

function rejectedReason(status: number) {
  if (status === 409) return "เครื่องนี้ไม่พร้อมให้เช่าในขณะนี้ (อาจเพิ่งถูกเช่าไป)";
  if (status === 404) return "ไม่พบเครื่องที่ต้องการเช่า";
  if (status === 403) return "ไม่มีสิทธิ์เช่าเครื่องนี้";
  return "คำขอเช่าไม่ถูกต้อง";
}

/**
 * Creates the Controller lease for a paid rental using the payment id as the
 * idempotency key. The Controller returns the existing lease for a repeated key,
 * which is what makes retries safe after an unknown outcome.
 */
export async function createControllerLease(
  call: ControllerCall,
  userId: string,
  payment: Pick<RentalPayment, "id" | "machineId" | "durationSeconds">,
): Promise<LeaseCreateOutcome> {
  try {
    const lease = await call<ControllerLeaseRecord>("/internal/leases", {
      method: "POST",
      body: JSON.stringify({
        userId,
        machineId: payment.machineId,
        durationSeconds: payment.durationSeconds,
        idempotencyKey: payment.id,
      }),
    });
    return { kind: "created", lease };
  } catch (error) {
    if (error instanceof ControllerError) {
      return error.status === 503
        ? { kind: "unknown" }
        : { kind: "rejected", reason: rejectedReason(error.status) };
    }
    return { kind: "unknown" };
  }
}

export async function createControllerLeaseWithRetry(
  call: ControllerCall,
  userId: string,
  payment: Pick<RentalPayment, "id" | "machineId" | "durationSeconds">,
  wait: (milliseconds: number) => Promise<void> = (milliseconds) =>
    new Promise((resolve) => setTimeout(resolve, milliseconds)),
) {
  let outcome = await createControllerLease(call, userId, payment);
  for (let attempt = 0; attempt < 2 && outcome.kind === "unknown"; attempt += 1) {
    await wait(250 * (attempt + 1));
    outcome = await createControllerLease(call, userId, payment);
  }
  return outcome;
}
