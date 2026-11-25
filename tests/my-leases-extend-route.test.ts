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

import { POST as postExtend } from "@/app/api/my-leases/[rentalId]/extend/route";
import {
  chargeRental,
  confirmRentalPayment,
  ensureRentalSchema,
} from "@/lib/rental-payments";

const IDEMPOTENCY_KEY = "b1f3a0d2-3c4e-4f5a-8b6c-7d8e9f0a1b2c";

function request(body: unknown) {
  return new Request("http://localhost/api/my-leases/lease-1/extend", {
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

function extensionRow(db: Database.Database) {
  return db
    .prepare("SELECT additional_seconds, amount_satang, status FROM rental_extension_payments")
    .get() as
    | { additional_seconds: number; amount_satang: number; status: string }
    | undefined;
}

function seedPayment(db: Database.Database, hours = 1) {
  const charged = chargeRental(db, {
    userId: "renter-1",
    machineId: "pc-1",
    machineName: "PC ONE",
    hourlyPriceSatang: 4_000,
    hours,
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

describe("POST /api/my-leases/[rentalId]/extend", () => {
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

  it("resolves the payment through paymentId, charges the extension and extends the lease", async () => {
    h.controllerCall.mockImplementation(async (path: string, init?: RequestInit) => {
      if (path === "/internal/leases/lease-1" && !init?.method) return lease(h.paymentId);
      if (path === "/internal/leases/lease-1/extend" && init?.method === "POST")
        return lease(h.paymentId, { pendingRevision: 2, state: "active" });
      throw new h.ControllerError(404);
    });

    const response = await postExtend(
      request({ additionalSeconds: 3_600, expectedRevision: 1, idempotencyKey: IDEMPOTENCY_KEY }),
      params(),
    );

    expect(response.status).toBe(200);
    const body = await response.json();
    expect(body).toMatchObject({ id: "lease-1", pendingRevision: 2, paymentId: h.paymentId });
    expect(h.controllerCall).toHaveBeenCalledWith("/internal/leases/lease-1");
    expect(extensionRow(h.db!)).toMatchObject({
      additional_seconds: 3_600,
      amount_satang: 4_000,
      status: "charged",
    });
    // 1h base rental was already charged: 100_000 - 4_000 - 4_000.
    expect(balance(h.db!)).toBe(92_000);
    expect(
      JSON.parse(
        String(
          h.controllerCall.mock.calls.find(
            ([path]) => path === "/internal/leases/lease-1/extend",
          )?.[1]?.body,
        ),
      ),
    ).toEqual({
      additionalSeconds: 3_600,
      expectedRevision: 1,
      idempotencyKey: IDEMPOTENCY_KEY,
    });
  });

  it("releases the extension charge when the Controller rejects it with 409", async () => {
    h.controllerCall.mockImplementation(async (path: string, init?: RequestInit) => {
      if (path === "/internal/leases/lease-1" && !init?.method) return lease(h.paymentId);
      throw new h.ControllerError(409);
    });

    const response = await postExtend(
      request({ additionalSeconds: 3_600, expectedRevision: 1, idempotencyKey: IDEMPOTENCY_KEY }),
      params(),
    );

    expect(response.status).toBe(409);
    expect((await response.json()).error).toContain("ไม่สามารถต่อเวลา");
    expect(extensionRow(h.db!)).toMatchObject({ status: "released" });
    expect(balance(h.db!)).toBe(96_000);
  });

  it("returns 404 without charging when the lease is unknown", async () => {
    h.controllerCall.mockRejectedValue(new h.ControllerError(404));

    const response = await postExtend(
      request({ additionalSeconds: 3_600, expectedRevision: 1, idempotencyKey: IDEMPOTENCY_KEY }),
      params(),
    );

    expect(response.status).toBe(404);
    // An older Controller has no GET /internal/leases/{id}; the route then falls
    // back to the legacy payment lookup by lease id and reports it as missing.
    expect((await response.json()).error).toBe("ไม่พบรายการชำระเงินสำหรับการเช่านี้");
    expect(extensionRow(h.db!)).toBeUndefined();
    expect(balance(h.db!)).toBe(96_000);
  });

  it("returns 403 without charging when the Controller reports another owner", async () => {
    h.controllerCall.mockRejectedValue(new h.ControllerError(403));

    const response = await postExtend(
      request({ additionalSeconds: 3_600, expectedRevision: 1, idempotencyKey: IDEMPOTENCY_KEY }),
      params(),
    );

    expect(response.status).toBe(403);
    expect((await response.json()).error).toContain("ไม่มีสิทธิ์");
    expect(extensionRow(h.db!)).toBeUndefined();
    expect(balance(h.db!)).toBe(96_000);
  });
});
