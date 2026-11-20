import { beforeEach, describe, expect, it, vi } from "vitest";

const h = vi.hoisted(() => {
  class MockControllerError extends Error {
    constructor(public status: number, public safeReleaseReason: string | null = null) {
      super("Controller unavailable");
    }
  }
  return {
    controllerRequest: vi.fn(),
    ControllerError: MockControllerError,
    role: "admin" as string | null,
    dbAll: vi.fn(),
  };
});

vi.mock("@/lib/auth", () => ({
  getAuth: async () => ({
    api: {
      getSession: async () =>
        h.role ? { user: { id: "admin-1", role: h.role } } : null,
    },
    options: {
      database: {
        prepare: () => ({ all: h.dbAll }),
      },
    },
  }),
}));

vi.mock("@/lib/controller-client", () => ({
  controllerRequest: h.controllerRequest,
  ControllerError: h.ControllerError,
}));

import { GET as getSnapshot } from "@/app/api/admin/pc-control/route";
import { POST as postPowerOff } from "@/app/api/admin/pc-control/[machineId]/power-off/route";
import { GET as getPairing, POST as postPairing } from "@/app/api/admin/pc-control/[machineId]/pairing/route";
import { POST as postLeaseTime } from "@/app/api/admin/pc-control/[machineId]/lease-time/route";
import { POST as postLeaseCancel } from "@/app/api/admin/pc-control/[machineId]/lease-cancel/route";
import { GET as getPairingStatus } from "@/app/api/admin/pc-control/[machineId]/pairing/[commandId]/route";
import { POST as postSunshine } from "@/app/api/admin/pc-control/[machineId]/sunshine/route";
import { GET as getSunshineStatus } from "@/app/api/admin/pc-control/[machineId]/sunshine/[commandId]/route";

function request(body?: unknown, headers: Record<string, string> = {}) {
  return new Request("http://localhost/api/admin/pc-control", {
    method: body === undefined ? "GET" : "POST",
    headers: { origin: "http://localhost", "content-type": "application/json", ...headers },
    body: body === undefined ? undefined : JSON.stringify(body),
  });
}

function params(machineId = "0123456789abcdef0123456789abcdef") {
  return { params: Promise.resolve({ machineId }) };
}

const machine = {
  machineId: "0123456789abcdef0123456789abcdef",
  displayName: "PC-01",
  approvalStatus: "approved",
  readiness: "ready",
};

const lease = {
  rentalId: "rental-1",
  userId: "user-1",
  state: "active",
  revision: 2,
  pendingRevision: null,
  startsAtUtc: "2026-11-17T00:00:00Z",
  expiresAtUtc: "2026-11-17T01:00:00Z",
  remainingSeconds: 3600,
};

describe("PC-control admin routes", () => {
  beforeEach(() => {
    h.controllerRequest.mockReset();
    h.dbAll.mockReset();
    h.role = "admin";
    process.env.BETTER_AUTH_URL = "http://localhost";
  });

  it("returns the machine snapshot enriched with renter names and no-store headers", async () => {
    h.controllerRequest.mockResolvedValueOnce({
      serverTimeUtc: "2026-11-17T00:10:00Z",
      machines: [{ machine, lease }],
    });
    h.dbAll.mockReturnValue([{ id: "user-1", name: "Alice", email: "alice@example.com" }]);

    const response = await getSnapshot(request());
    const body = await response.json();

    expect(response.status).toBe(200);
    expect(response.headers.get("cache-control")).toBe("no-store");
    expect(h.controllerRequest).toHaveBeenCalledWith("/internal/pc-control", expect.anything());
    expect(body.machines[0].lease).toMatchObject({ userName: "Alice", userEmail: "alice@example.com" });
  });

  it("forbids non-admin snapshot access", async () => {
    h.role = "user";
    const response = await getSnapshot(request());
    expect(response.status).toBe(403);
    expect(h.controllerRequest).not.toHaveBeenCalled();
  });

  it("validates and forwards forced power off", async () => {
    h.controllerRequest.mockResolvedValueOnce({ commandId: "command-1", status: "requested" });

    const invalid = await postPowerOff(request({ requestId: "ok", extra: true }), params());
    expect(invalid.status).toBe(400);

    const response = await postPowerOff(request({ requestId: "power-1" }), params());
    expect(response.status).toBe(200);
    const call = h.controllerRequest.mock.calls[0];
    expect(call[0]).toBe(`/internal/machines/0123456789abcdef0123456789abcdef/power-off`);
    expect(JSON.parse(call[1].body)).toEqual({ requestId: "power-1" });
  });

  it("validates and forwards lease time adjustments", async () => {
    await postLeaseTime(request({ deltaSeconds: 0, expectedRevision: 2, idempotencyKey: "key" }), params());
    expect(h.controllerRequest).not.toHaveBeenCalled();

    h.controllerRequest.mockResolvedValueOnce({});
    const response = await postLeaseTime(
      request({ deltaSeconds: -1800, expectedRevision: 2, idempotencyKey: "key" }),
      params(),
    );
    expect(response.status).toBe(200);
    const call = h.controllerRequest.mock.calls[0];
    expect(call[0]).toContain("/lease-time");
    expect(JSON.parse(call[1].body)).toEqual({ deltaSeconds: -1800, expectedRevision: 2, idempotencyKey: "key" });
  });

  it("validates pairing PINs and forwards admin pairing requests", async () => {
    const pairingId = "00000000000000000000000000000001";
    const badPin = await postPairing(request({ pairingId, pin: "12a4", idempotencyKey: "key" }), params());
    expect(badPin.status).toBe(400);

    h.controllerRequest.mockResolvedValueOnce({ commandId: "pair-1", status: "queued" });
    const response = await postPairing(request({ pairingId, pin: "1234", idempotencyKey: "key" }), params());
    expect(response.status).toBe(200);
    const call = h.controllerRequest.mock.calls[0];
    expect(call[0]).toContain("/pairing");
    expect(JSON.parse(call[1].body)).toEqual({ pairingId, pin: "1234", idempotencyKey: "key" });
  });

  it("loads pairing offer and status for the current machine", async () => {
    h.controllerRequest.mockResolvedValueOnce(null).mockResolvedValueOnce({ commandId: "pair-1", status: "queued" });

    const offer = await getPairing(request(), params());
    expect(offer.status).toBe(200);
    expect(h.controllerRequest.mock.calls[0][0]).toContain("/pairing");

    const status = await getPairingStatus(
      request(),
      { params: Promise.resolve({ machineId: "0123456789abcdef0123456789abcdef", commandId: "pair-1" }) },
    );
    expect(status.status).toBe(200);
    expect(h.controllerRequest.mock.calls[1][0]).toContain("/pairing/pair-1");
  });

  it("validates and forwards lease cancellation", async () => {
    await postLeaseCancel(request({ expectedRevision: 2, idempotencyKey: "key", requestId: "cancel-1", extra: true }), params());
    expect(h.controllerRequest).not.toHaveBeenCalled();

    h.controllerRequest.mockResolvedValueOnce({});
    const response = await postLeaseCancel(
      request({ expectedRevision: 2, idempotencyKey: "key", requestId: "cancel-1" }),
      params(),
    );
    expect(response.status).toBe(200);
    const call = h.controllerRequest.mock.calls[0];
    expect(call[0]).toContain("/lease-cancel");
    expect(JSON.parse(call[1].body)).toEqual({ expectedRevision: 2, idempotencyKey: "key", requestId: "cancel-1" });
  });

  it("validates and forwards machine-scoped Sunshine maintenance commands", async () => {
    const badAction = await postSunshine(request({ action: "explode", force: false }), params());
    expect(badAction.status).toBe(400);
    const badForce = await postSunshine(request({ action: "restart", force: "yes" }), params());
    expect(badForce.status).toBe(400);
    const extraField = await postSunshine(request({ action: "restart", force: false, extra: true }), params());
    expect(extraField.status).toBe(400);
    expect(h.controllerRequest).not.toHaveBeenCalled();

    h.controllerRequest.mockResolvedValueOnce({
      commandId: "sunshine-1",
      action: "restart",
      status: "requested",
      expiresAtUtc: "2026-11-17T00:11:00Z",
    });
    const response = await postSunshine(
      request({ action: "restart", force: true, requestId: "sunshine-request-1" }),
      params(),
    );
    expect(response.status).toBe(200);
    expect(await response.json()).toMatchObject({ commandId: "sunshine-1", status: "requested" });
    const call = h.controllerRequest.mock.calls[0];
    expect(call[0]).toBe("/internal/admin/pc-control/0123456789abcdef0123456789abcdef/sunshine");
    expect(JSON.parse(call[1].body)).toEqual({ action: "restart", force: true, requestId: "sunshine-request-1" });
  });

  it("loads the Sunshine maintenance command status for the current machine", async () => {
    h.controllerRequest.mockResolvedValueOnce({
      commandId: "sunshine-1",
      action: "unpair",
      status: "applied",
      expiresAtUtc: "2026-11-17T00:11:00Z",
    });

    const response = await getSunshineStatus(request(), {
      params: Promise.resolve({
        machineId: "0123456789abcdef0123456789abcdef",
        commandId: "sunshine-1",
      }),
    });
    expect(response.status).toBe(200);
    expect(h.controllerRequest.mock.calls[0][0]).toBe(
      "/internal/admin/pc-control/0123456789abcdef0123456789abcdef/sunshine/sunshine-1",
    );
    expect(await response.json()).toMatchObject({ status: "applied" });
  });
});
