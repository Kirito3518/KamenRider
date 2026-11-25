import { beforeEach, describe, expect, it, vi } from "vitest";

const { controllerCall, settleRentalRefunds, listRentalPayments } = vi.hoisted(() => ({
  controllerCall: vi.fn(),
  settleRentalRefunds: vi.fn(),
  listRentalPayments: vi.fn(),
}));

type MockAction = (
  actor: { id: string; role: string },
  call: (path: string) => Promise<unknown>,
) => Promise<unknown>;

vi.mock("@/lib/machine-api", () => ({
  segment: (value: string) => encodeURIComponent(value),
  machineApi: async (
    _request: Request,
    _admin: boolean,
    action: MockAction,
  ) =>
    Response.json(
      await action(
        { id: "renter-1", role: "user" },
        controllerCall as (path: string) => Promise<unknown>,
      ),
    ),
}));

vi.mock("@/lib/rental-payments", () => ({
  getMemberBalance: () => 95_000,
  listRentalPayments,
  reconcilePendingRentals: async () => [],
  settleRentalRefunds,
}));

vi.mock("@/lib/rental-service", () => ({
  authDatabase: async () => ({}),
  createControllerLease: async () => ({ kind: "unknown" }),
}));

import { GET } from "@/app/api/my-leases/route";

const HEARTBEAT_AT = "2026-11-08T03:04:05.000Z";

/** Controller lease records; the id is random and NOT the payment id. */
const LEASES = [
  {
    id: "lease-1",
    userId: "renter-1",
    machineId: "pc-one",
    state: "active",
    revision: 1,
    startsAtUtc: "2026-11-08T02:00:00.000Z",
    expiresAtUtc: "2026-11-08T04:00:00.000Z",
    pendingRevision: null,
    paymentId: "payment-1",
  },
  {
    id: "lease-2",
    userId: "renter-1",
    machineId: "pc-offline",
    state: "preparing",
    revision: 1,
    startsAtUtc: null,
    expiresAtUtc: null,
    pendingRevision: null,
    paymentId: "payment-2",
  },
  {
    id: "lease-3",
    userId: "renter-1",
    machineId: "pc-unknown",
    state: "preparing",
    revision: 1,
    startsAtUtc: null,
    expiresAtUtc: null,
    pendingRevision: null,
    paymentId: null,
  },
  {
    id: "lease-4",
    userId: "renter-1",
    machineId: "pc-unrecognized",
    state: "preparing",
    revision: 1,
    startsAtUtc: null,
    expiresAtUtc: null,
    pendingRevision: null,
    paymentId: null,
  },
];

const PAYMENT = {
  id: "payment-1",
  userId: "renter-1",
  machineId: "pc-one",
  machineName: "PC ONE",
  hourlyPriceSatang: 1_000,
  durationSeconds: 3_600,
  amountSatang: 1_000,
  extensionSeconds: 0,
  extensionSatang: 0,
  refundedSatang: 0,
  status: "confirmed" as const,
  rejectReason: null,
  refundedAt: null,
  refundNote: null,
  settledAt: null,
  createdAt: 1_760_000_000_000,
  updatedAt: 1_760_000_000_000,
};

function controllerMachine(
  machineId: string,
  readiness: string,
  lastSeenAt: string | null,
) {
  return {
    machineId,
    approvalStatus: "approved",
    displayName: machineId,
    hourlyPriceMinorUnits: 100,
    localIp: "192.168.1.4",
    macAddress: "001122334455",
    moonlightPort: 47989,
    sunshinePort: 47990,
    configRevision: 3,
    lastSeenAt,
    bootId: "boot-1",
    readiness,
    agentVersion: "1.0.0",
    approvedBy: "admin-1",
    approvedAt: "2026-11-01T00:00:00.000Z",
    appliedConfigRevision: 3,
    publicKeyFingerprint: "secret-management-fingerprint",
    vncLocalIp: "192.168.1.5",
    activeAdminVncSessions: 0,
    canRent: false,
    readinessDiagnostics: null,
  };
}

describe("GET /api/my-leases", () => {
  beforeEach(() => {
    controllerCall.mockReset();
    settleRentalRefunds.mockReset();
    settleRentalRefunds.mockReturnValue({ refundedSatang: 0, refundedPayments: 0 });
    listRentalPayments.mockReset();
    listRentalPayments.mockReturnValue([PAYMENT]);
    controllerCall.mockImplementation(async (path: string) => {
      if (path === "/internal/users/renter-1/leases") return LEASES;
      if (path === "/internal/machines")
        return [
          controllerMachine("pc-one", "ready", HEARTBEAT_AT),
          controllerMachine(
            "pc-offline",
            "unreachable",
            "2026-11-08T02:00:00.000Z",
          ),
          controllerMachine("pc-unknown", "unknown", null),
          controllerMachine("pc-unrecognized", "maintenance", HEARTBEAT_AT),
        ];
      const connection = path.match(/^\/internal\/leases\/([^/]+)\/connection$/);
      if (connection) return { address: null, stale: false };
      throw new Error(`Unexpected Controller request: ${path}`);
    });
  });

  it("returns the lease feed and wallet balance after settling refunds", async () => {
    const response = await GET(new Request("http://localhost/api/my-leases"));
    const data = await response.json();

    expect(controllerCall).toHaveBeenCalledWith(
      "/internal/users/renter-1/leases",
    );
    expect(controllerCall).toHaveBeenCalledWith("/internal/machines");
    expect(settleRentalRefunds).toHaveBeenCalledWith(
      expect.anything(),
      LEASES,
      { userId: "renter-1" },
    );
    expect(data).toMatchObject({ balanceSatang: 95_000 });
    expect(data.leases.map(({ id, agent }: (typeof data.leases)[number]) => ({ id, agent }))).toEqual([
      {
        id: "lease-1",
        agent: { reachability: "reachable", lastSeenAt: HEARTBEAT_AT },
      },
      {
        id: "lease-2",
        agent: {
          reachability: "unreachable",
          lastSeenAt: "2026-11-08T02:00:00.000Z",
        },
      },
      {
        id: "lease-3",
        agent: { reachability: "unknown", lastSeenAt: null },
      },
      {
        id: "lease-4",
        agent: { reachability: "unknown", lastSeenAt: HEARTBEAT_AT },
      },
    ]);

    const responseData = JSON.stringify(data);
    for (const privateValue of [
      "192.168.1.4",
      "192.168.1.5",
      "001122334455",
      "47989",
      "47990",
      "secret-management-fingerprint",
      "admin-1",
      "boot-1",
    ])
      expect(responseData).not.toContain(privateValue);
  });

  it("attaches the rental payment by paymentId even though the lease id differs", async () => {
    const response = await GET(new Request("http://localhost/api/my-leases"));
    const data = await response.json();

    const paidLease = data.leases.find(
      (lease: { id: string }) => lease.id === "lease-1",
    );
    expect(paidLease.paymentId).toBe("payment-1");
    expect(paidLease.payment).toMatchObject({
      hourlyPriceSatang: 1_000,
      paidSatang: 1_000,
      refundedSatang: 0,
      extendable: true,
    });

    // Leases without a matching payment keep a null payment instead of silently
    // borrowing another rental's money.
    expect(
      data.leases.find((lease: { id: string }) => lease.id === "lease-3").payment,
    ).toBeNull();
    expect(
      data.leases.find((lease: { id: string }) => lease.id === "lease-4").payment,
    ).toBeNull();
  });

  it("lists a pending payment as an idempotent pre-lease entry", async () => {
    listRentalPayments.mockReturnValue([
      PAYMENT,
      {
        ...PAYMENT,
        id: "payment-pending",
        machineId: "pc-unrecognized",
        machineName: "PC UNRECOGNIZED",
        status: "pending",
      },
    ]);
    const response = await GET(new Request("http://localhost/api/my-leases"));
    const data = await response.json();

    expect(data.leases[0]).toMatchObject({
      id: "payment-pending",
      state: "pending",
      machineDisplayName: "PC UNRECOGNIZED",
    });
    // The pending entry is synthetic and must not consume the Controller lease
    // payment: the active lease still gets its own payment attached.
    expect(
      data.leases.find((lease: { id: string }) => lease.id === "lease-1").payment,
    ).not.toBeNull();
  });

  it("exposes a verified public connect address only for an active lease", async () => {
    controllerCall.mockImplementation(async (path: string) => {
      if (path === "/internal/users/renter-1/leases") return LEASES.slice(0, 2);
      if (path === "/internal/machines")
        return [
          {
            ...controllerMachine("pc-one", "ready", HEARTBEAT_AT),
            displayName: "DESKTOP-ONE",
          },
          controllerMachine("pc-offline", "unreachable", HEARTBEAT_AT),
        ];
      const connection = path.match(
        /^\/internal\/leases\/([^/]+)\/connection$/,
      );
      if (connection)
        return {
          address: "203.0.113.7:47989",
          stale: true,
          extra: "must not reach the browser",
        };
      throw new Error(`Unexpected Controller request: ${path}`);
    });
    listRentalPayments.mockReturnValue([]);

    const response = await GET(new Request("http://localhost/api/my-leases"));
    const data = await response.json();
    const leases = data.leases;

    expect(leases[0].connection).toEqual({
      address: "203.0.113.7:47989",
      stale: true,
    });
    expect(leases[0].machineDisplayName).toBe("DESKTOP-ONE");
    expect(leases[1].connection).toEqual({ address: null, stale: false });
    expect(controllerCall).toHaveBeenCalledWith(
      "/internal/leases/lease-1/connection",
    );
    expect(
      controllerCall.mock.calls.some(([path]) =>
        String(path).includes("lease-2/connection"),
      ),
    ).toBe(false);
    expect(JSON.stringify(data)).not.toContain("must not reach the browser");
  });
});
