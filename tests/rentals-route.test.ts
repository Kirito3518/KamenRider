import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import Database from "better-sqlite3";

const h = vi.hoisted(() => {
  class MockControllerError extends Error {
    constructor(public status: number) {
      super("Controller unavailable");
    }
  }
  return {
    controllerRequest: vi.fn(),
    ControllerError: MockControllerError,
    session: null as null | { user: { id: string; role: string } },
    db: null as Database.Database | null,
  };
});

vi.mock("@/lib/auth", () => ({
  getAuth: async () => ({
    api: {
      getSession: async () => h.session,
    },
    options: { database: h.db },
  }),
}));

vi.mock("@/lib/controller-client", () => ({
  controllerRequest: h.controllerRequest,
  ControllerError: h.ControllerError,
}));

import { GET as getRentals, POST as postRentals } from "@/app/api/rentals/route";
import { GET as getAdminRentals } from "@/app/api/admin/rentals/route";
import {
  chargeRental,
  confirmRentalPayment,
  ensureRentalSchema,
} from "@/lib/rental-payments";

const MACHINE = {
  machineId: "pc-1",
  approvalStatus: "approved",
  displayName: "PC ONE",
  hourlyPriceMinorUnits: 1_000,
  canRent: true,
  readiness: "ready",
  lastSeenAt: "2026-11-18T03:00:00.000Z",
};

const session = { user: { id: "renter-1", role: "user" } };
const adminSession = { user: { id: "admin-1", role: "admin" } };

function request(body?: unknown, origin = "http://localhost", path = "/api/rentals") {
  return new Request(`http://localhost${path}`, {
    method: body === undefined ? "GET" : "POST",
    headers: { origin, "content-type": "application/json" },
    body: body === undefined ? undefined : JSON.stringify(body),
  });
}

function seedPayment(db: Database.Database, requestId = crypto.randomUUID()) {
  const charged = chargeRental(db, {
    userId: "renter-1",
    machineId: "pc-1",
    machineName: "PC ONE",
    hourlyPriceSatang: 1_000,
    hours: 1,
    requestId,
  });
  return confirmRentalPayment(db, charged.payment.id);
}

function balance(db: Database.Database) {
  return (
    db.prepare("SELECT balanceSatang FROM user WHERE id='renter-1'").get() as {
      balanceSatang: number;
    }
  ).balanceSatang;
}

function payment(db: Database.Database) {
  return db
    .prepare("SELECT id, status, amount_satang, refunded_satang, settled_at FROM rental_payments")
    .get() as
    | {
        id: string;
        status: string;
        amount_satang: number;
        refunded_satang: number;
        settled_at: number | null;
      }
    | undefined;
}

describe("POST /api/rentals", () => {
  beforeEach(() => {
    process.env.BETTER_AUTH_URL = "http://localhost";
    h.session = session;
    h.controllerRequest.mockReset();
    h.controllerRequest.mockImplementation(async (path: string, init?: RequestInit) => {
      if (path === "/internal/machines") return [MACHINE];
      if (path === "/internal/leases" && init?.method === "POST") {
        const body = JSON.parse(String(init.body)) as {
          userId: string;
          machineId: string;
          durationSeconds: number;
          idempotencyKey: string;
        };
        // The real Controller mints a random lease id and reports the create
        // idempotency key (the payment id) separately as paymentId.
        return {
          id: "controller-lease-" + crypto.randomUUID(),
          paymentId: body.idempotencyKey,
          userId: body.userId,
          machineId: body.machineId,
          state: "preparing",
          revision: 1,
          startsAtUtc: null,
          expiresAtUtc: null,
          pendingRevision: null,
          endedAtUtc: null,
        };
      }
      throw new h.ControllerError(404);
    });
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
    h.db = db;
  });

  afterEach(() => {
    h.db?.close();
    h.db = null;
    h.session = null;
  });

  it("requires a session and a trusted origin", async () => {
    h.session = null;
    expect((await postRentals(request({ machineId: "pc-1", hours: 1, requestId: crypto.randomUUID() }))).status).toBe(401);
    expect(h.controllerRequest).not.toHaveBeenCalled();

    h.session = session;
    const crossOrigin = await postRentals(
      request(
        { machineId: "pc-1", hours: 1, requestId: crypto.randomUUID() },
        "https://evil.example",
      ),
    );
    expect(crossOrigin.status).toBe(403);
    expect(h.controllerRequest).not.toHaveBeenCalled();
  });

  it("validates whole hours and the request id before charging", async () => {
    for (const body of [
      { machineId: "pc-1", hours: 0, requestId: crypto.randomUUID() },
      { machineId: "pc-1", hours: 25, requestId: crypto.randomUUID() },
      { machineId: "pc-1", hours: 1.5, requestId: crypto.randomUUID() },
      { machineId: "pc-1", hours: 1, requestId: "not-a-uuid" },
      { machineId: "pc-1", hours: 1, requestId: crypto.randomUUID(), extra: true },
    ]) {
      const response = await postRentals(request(body));
      expect(response.status).toBe(400);
    }
    expect(payment(h.db!)).toBeUndefined();
  });

  it("charges the owner, creates the Controller lease and confirms the payment", async () => {
    const requestId = crypto.randomUUID();
    const response = await postRentals(request({ machineId: "pc-1", hours: 2, requestId }));
    const body = await response.json();

    expect(response.status).toBe(200);
    expect(body).toMatchObject({ state: "preparing", balanceSatang: 98_000, repeated: false });
    expect(await payment(h.db!)).toMatchObject({
      status: "confirmed",
      amount_satang: 2_000,
      refunded_satang: 0,
    });
    expect(balance(h.db!)).toBe(98_000);

    const createCall = h.controllerRequest.mock.calls.find(([path]) => path === "/internal/leases");
    expect(createCall).toBeDefined();
    expect(JSON.parse(String(createCall?.[1]?.body))).toEqual({
      userId: "renter-1",
      machineId: "pc-1",
      durationSeconds: 7_200,
      idempotencyKey: body.rentalId,
    });

    const repeated = await postRentals(request({ machineId: "pc-1", hours: 2, requestId }));
    expect(repeated.status).toBe(200);
    expect(balance(h.db!)).toBe(98_000);
    expect(
      (
        h.db!
          .prepare("SELECT count(*) AS count FROM rental_payments")
          .get() as { count: number }
      ).count,
    ).toBe(1);
    expect(
      h.controllerRequest.mock.calls.filter(([path]) => path === "/internal/leases"),
    ).toHaveLength(1);
  });

  it("returns 402 without creating a lease when the balance is insufficient", async () => {
    h.db!.prepare("UPDATE user SET balanceSatang = 500 WHERE id = 'renter-1'").run();
    const response = await postRentals(
      request({ machineId: "pc-1", hours: 1, requestId: crypto.randomUUID() }),
    );
    const body = await response.json();
    expect(response.status).toBe(402);
    expect(body.code).toBe("insufficient-funds");
    expect(h.controllerRequest.mock.calls.some(([path]) => path === "/internal/leases")).toBe(false);
    expect(payment(h.db!)).toBeUndefined();
    expect(balance(h.db!)).toBe(500);
  });

  it("refuses a machine that is not rentable without charging", async () => {
    h.controllerRequest.mockImplementation(async (path: string) =>
      path === "/internal/machines" ? [{ ...MACHINE, canRent: false }] : Promise.reject(new h.ControllerError(404)),
    );
    const response = await postRentals(
      request({ machineId: "pc-1", hours: 1, requestId: crypto.randomUUID() }),
    );
    expect(response.status).toBe(409);
    expect(payment(h.db!)).toBeUndefined();
    expect(balance(h.db!)).toBe(100_000);
  });

  it("refuses a machine whose price is not whole baht", async () => {
    h.controllerRequest.mockImplementation(async (path: string) =>
      path === "/internal/machines" ? [{ ...MACHINE, hourlyPriceMinorUnits: 1_050 }] : Promise.reject(new h.ControllerError(404)),
    );
    const response = await postRentals(
      request({ machineId: "pc-1", hours: 1, requestId: crypto.randomUUID() }),
    );
    expect(response.status).toBe(409);
    expect(payment(h.db!)).toBeUndefined();
  });

  it("refunds in full when the Controller refuses the lease", async () => {
    h.controllerRequest.mockImplementation(async (path: string) => {
      if (path === "/internal/machines") return [MACHINE];
      throw new h.ControllerError(409);
    });
    const response = await postRentals(
      request({ machineId: "pc-1", hours: 1, requestId: crypto.randomUUID() }),
    );
    expect(response.status).toBe(409);
    expect(await payment(h.db!)).toMatchObject({ status: "rejected", refunded_satang: 1_000 });
    expect(balance(h.db!)).toBe(100_000);
  });

  it("keeps a pending payment when the Controller outcome is unknown", async () => {
    h.controllerRequest.mockImplementation(async (path: string) => {
      if (path === "/internal/machines") return [MACHINE];
      throw new h.ControllerError(503);
    });
    const response = await postRentals(
      request({ machineId: "pc-1", hours: 1, requestId: crypto.randomUUID() }),
    );
    expect(response.status).toBe(503);
    expect(await payment(h.db!)).toMatchObject({ status: "pending", refunded_satang: 0 });
    expect(balance(h.db!)).toBe(99_000);
  });
});

describe("rental history routes", () => {
  beforeEach(() => {
    process.env.BETTER_AUTH_URL = "http://localhost";
    h.session = session;
    h.controllerRequest.mockReset();
    const db = new Database(":memory:");
    db.exec(
      "CREATE TABLE user(id TEXT PRIMARY KEY, name TEXT, email TEXT, balanceSatang INTEGER NOT NULL DEFAULT 0, updatedAt TEXT);",
    );
    db.prepare("INSERT INTO user(id, name, email, balanceSatang, updatedAt) VALUES (?, ?, ?, ?, ?)").run(
      "renter-1",
      "Renter One",
      "renter@example.com",
      100_000,
      new Date(0).toISOString(),
    );
    ensureRentalSchema(db);
    h.db = db;
  });

  afterEach(() => {
    h.db?.close();
    h.db = null;
    h.session = null;
    vi.useRealTimers();
  });

  it("settles the unused time of ended leases before returning history", async () => {
    const paymentRow = seedPayment(h.db!);
    const startsAt = "2026-11-18T03:00:00.000Z";
    h.controllerRequest.mockImplementation(async (path: string) => {
      if (path === "/internal/users/renter-1/leases")
        return [
          {
            // The Controller lease id is random and differs from the payment id;
            // the create idempotency key is exposed as paymentId.
            id: "lease-from-controller",
            paymentId: paymentRow.id,
            userId: "renter-1",
            machineId: "pc-1",
            state: "completed",
            revision: 1,
            startsAtUtc: startsAt,
            expiresAtUtc: "2026-11-18T04:00:00.000Z",
            pendingRevision: null,
            endedAtUtc: "2026-11-18T04:00:00.000Z",
          },
        ];
      if (path === "/internal/machines") return [MACHINE];
      throw new h.ControllerError(404);
    });

    const response = await getRentals(request());
    expect(response.status).toBe(200);
    const body = await response.json();
    expect(body.controllerAvailable).toBe(true);
    expect(body.summary.totalRentals).toBe(1);
    // The payment is joined through paymentId: money fields are present even
    // though the Controller lease id is not the payment id.
    expect(body.entries[0]).toMatchObject({
      rentalId: "lease-from-controller",
      machineName: "PC ONE",
      state: "completed",
      hourlyPriceSatang: 1_000,
      paidSatang: 1_000,
      refundedSatang: 0,
      netSatang: 1_000,
    });
    expect(balance(h.db!)).toBe(99_000);
  });

  it("falls back to payment records when the Controller is unavailable", async () => {
    seedPayment(h.db!);
    h.controllerRequest.mockRejectedValue(new h.ControllerError(503));
    const response = await getRentals(request());
    expect(response.status).toBe(200);
    const body = await response.json();
    expect(body.controllerAvailable).toBe(false);
    expect(body.entries).toHaveLength(1);
    expect(body.entries[0]).toMatchObject({ machineName: "PC ONE", state: "unknown" });
    expect(body.balanceSatang).toBe(99_000);
  });

  it("keeps admin history behind the admin role and enriches renter identity", async () => {
    const paymentRow = seedPayment(h.db!);
    h.session = session;
    expect((await getAdminRentals(request(undefined, "http://localhost", "/api/admin/rentals"))).status).toBe(403);

    h.session = adminSession;
    h.controllerRequest.mockImplementation(async (path: string) => {
      if (path.startsWith("/internal/leases")) {
        expect(path).toBe("/internal/leases?take=500");
        return [
          {
            id: "lease-from-controller",
            paymentId: paymentRow.id,
            userId: "renter-1",
            machineId: "pc-1",
            state: "active",
            revision: 1,
            startsAtUtc: "2026-11-18T03:00:00.000Z",
            expiresAtUtc: "2026-11-18T04:00:00.000Z",
            pendingRevision: null,
            endedAtUtc: null,
          },
        ];
      }
      if (path === "/internal/machines") return [MACHINE];
      throw new h.ControllerError(404);
    });

    const response = await getAdminRentals(request(undefined, "http://localhost", "/api/admin/rentals"));
    expect(response.status).toBe(200);
    const body = await response.json();
    expect(body.summary).toMatchObject({ totalRentals: 1, activeRentals: 1, netSatang: 1_000 });
    expect(body.entries[0]).toMatchObject({
      userName: "Renter One",
      userEmail: "renter@example.com",
      state: "active",
      paidSatang: 1_000,
    });
  });
});
