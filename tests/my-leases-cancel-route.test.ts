import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import Database from "better-sqlite3";

const h = vi.hoisted(() => {
  class MockControllerError extends Error {
    constructor(public status: number) {
      super("Controller unavailable");
    }
  }
  return {
    controllerCall: vi.fn(),
    ControllerError: MockControllerError,
    db: null as Database.Database | null,
    paymentId: "" as string,
  };
});

vi.mock("@/lib/machine-api", () => ({
  segment: (value: string) => encodeURIComponent(value),
  integer: (value: unknown, min = 0, max = Number.MAX_SAFE_INTEGER) => {
    if (!Number.isSafeInteger(value) || Number(value) < min || Number(value) > max)
      throw new TypeError("Invalid integer");
    return Number(value);
  },
  text: (value: unknown) => {
    if (typeof value !== "string" || !value.trim() || value.length > 128)
      throw new TypeError("Invalid text");
    return value;
  },
  machineApi: async (
    _request: Request,
    _admin: boolean,
    action: (
      actor: { id: string; role: string },
      call: (path: string, init?: RequestInit) => Promise<unknown>,
    ) => Promise<unknown>,
    _publicAccess = false,
    errorResponse?: (error: unknown) => Response | null,
  ) => {
    void _publicAccess;
    try {
      return Response.json(
        await action({ id: "renter-1", role: "user" }, h.controllerCall),
      );
    } catch (error) {
      const mapped = errorResponse?.(error);
      if (mapped) return mapped;
      throw error;
    }
  },
}));

vi.mock("@/lib/controller-client", () => ({
  ControllerError: h.ControllerError,
}));

vi.mock("@/lib/rental-service", () => ({
  authDatabase: async () => h.db,
}));

import { POST as postCancel } from "@/app/api/my-leases/[rentalId]/cancel/route";
import {
  chargeRental,
  confirmRentalPayment,
  ensureRentalSchema,
} from "@/lib/rental-payments";

const IDEMPOTENCY_KEY = "0f7f5c3a-9d2b-4c1e-8a6f-5e4d3c2b1a09";

function request(body: unknown) {
  return new Request("http://localhost/api/my-leases/lease-1/cancel", {
    method: "POST",
    headers: { origin: "http://localhost", "content-type": "application/json" },
    body: JSON.stringify(body),
  });
}

function params() {
  return { params: Promise.resolve({ rentalId: "lease-1" }) };
}

function balance(db: Database.Database) {
  return (
    db.prepare("SELECT balanceSatang FROM user WHERE id='renter-1'").get() as {
      balanceSatang: number;
    }
  ).balanceSatang;
}

function paymentRow(db: Database.Database, paymentId: string) {
  return db
    .prepare("SELECT refunded_satang, settled_at FROM rental_payments WHERE id=?")
    .get(paymentId) as { refunded_satang: number; settled_at: number | null };
}

function seedPayment(db: Database.Database, hourlyPriceSatang = 4_000) {
  const charged = chargeRental(db, {
    userId: "renter-1",
    machineId: "pc-1",
    machineName: "PC ONE",
    hourlyPriceSatang,
    hours: 1,
    requestId: crypto.randomUUID(),
  });
  return confirmRentalPayment(db, charged.payment.id);
}

function lease(paymentId: string | null, overrides: Record<string, unknown> = {}) {
  return {
    id: "lease-1",
    userId: "renter-1",
    machineId: "pc-1",
    state: "active",
    revision: 1,
    startsAtUtc: "2026-11-18T03:00:00.000Z",
    expiresAtUtc: "2026-11-18T04:00:00.000Z",
    pendingRevision: null,
    endedAtUtc: null,
    paymentId,
    ...overrides,
  };
}

describe("POST /api/my-leases/[rentalId]/cancel", () => {
  beforeEach(() => {
    process.env.BETTER_AUTH_URL = "http://localhost";
    h.controllerCall.mockReset();
    const db = new Database(":memory:");
    db.exec(
      "CREATE TABLE user(id TEXT PRIMARY KEY, balanceSatang INTEGER NOT NULL DEFAULT 0, updatedAt TEXT);",
    );
    db.prepare("INSERT INTO user(id, balanceSatang, updatedAt) VALUES (?, ?, ?)").run(
      "renter-1",
      100_000,
      new Date(0).toISOString(),
    );
    ensureRentalSchema(db);
    const payment = seedPayment(db);
    h.paymentId = payment.id;
    h.db = db;
  });

  afterEach(() => {
    h.db?.close();
    h.db = null;
  });

  it("credits the prorated refund through paymentId and returns the new balance", async () => {
    h.controllerCall.mockImplementation(async (path: string, init?: RequestInit) => {
      if (path === "/internal/leases/lease-1" && !init?.method) return lease(h.paymentId);
      if (path === "/internal/leases/lease-1/cancel" && init?.method === "POST")
        return lease(h.paymentId, {
          state: "ending",
          // Half of the paid hour was used: the other half is refunded.
          endedAtUtc: "2026-11-18T03:30:00.000Z",
        });
      throw new h.ControllerError(404);
    });

    const response = await postCancel(
      request({ expectedRevision: 1, idempotencyKey: IDEMPOTENCY_KEY }),
      params(),
    );

    expect(response.status).toBe(200);
    const body = await response.json();
    expect(body.lease).toMatchObject({ id: "lease-1", state: "ending" });
    expect(body.refundedSatang).toBe(2_000);
    expect(body.paymentRefundedSatang).toBe(2_000);
    expect(body.balanceSatang).toBe(98_000);
    expect(paymentRow(h.db!, h.paymentId)).toMatchObject({
      refunded_satang: 2_000,
    });
    expect(paymentRow(h.db!, h.paymentId).settled_at).not.toBeNull();
    expect(balance(h.db!)).toBe(98_000);
    // The cancel went through the Controller lease id, while the refund row was
    // found through the payment id reported as paymentId.
    expect(
      h.controllerCall.mock.calls.some(([path]) => path === "/internal/leases/lease-1"),
    ).toBe(true);
    expect(h.controllerCall).toHaveBeenCalledWith(
      "/internal/leases/lease-1/cancel",
      expect.objectContaining({ method: "POST" }),
    );
  });

  it("returns 404 without settling when the lease lookup fails", async () => {
    h.controllerCall.mockRejectedValue(new h.ControllerError(404));

    const response = await postCancel(
      request({ expectedRevision: 1, idempotencyKey: IDEMPOTENCY_KEY }),
      params(),
    );

    expect(response.status).toBe(404);
    expect((await response.json()).error).toBe("ไม่พบรายการเช่านี้");
    expect(paymentRow(h.db!, h.paymentId).refunded_satang).toBe(0);
    expect(balance(h.db!)).toBe(96_000);
  });

  it("falls back to the legacy cancel path when the Controller has no lease-lookup route", async () => {
    // An older Controller (rolling deployment) has no GET /internal/leases/{id}.
    h.controllerCall.mockImplementation(async (path: string, init?: RequestInit) => {
      if (path === "/internal/leases/lease-1" && !init?.method)
        throw new h.ControllerError(404);
      if (path === "/internal/leases/lease-1/cancel" && init?.method === "POST")
        return lease(null, { state: "ending" });
      throw new h.ControllerError(404);
    });

    const response = await postCancel(
      request({ expectedRevision: 1, idempotencyKey: IDEMPOTENCY_KEY }),
      params(),
    );

    expect(response.status).toBe(200);
    const body = await response.json();
    expect(body.refundedSatang).toBe(0);
    expect(body.balanceSatang).toBe(96_000);
    expect(body.lease).toMatchObject({ id: "lease-1", state: "ending" });
  });

  it("returns 503 when the Controller is unavailable", async () => {
    h.controllerCall.mockRejectedValue(new h.ControllerError(503));

    const response = await postCancel(
      request({ expectedRevision: 1, idempotencyKey: IDEMPOTENCY_KEY }),
      params(),
    );

    expect(response.status).toBe(503);
    expect(balance(h.db!)).toBe(96_000);
  });
});
