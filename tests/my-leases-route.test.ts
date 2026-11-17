import { beforeEach, describe, expect, it, vi } from "vitest";

const { controllerCall } = vi.hoisted(() => ({ controllerCall: vi.fn() }));

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

import { GET } from "@/app/api/my-leases/route";

const HEARTBEAT_AT = "2026-11-08T03:04:05.000Z";

const LEASES = [
  {
    id: "rental-1",
    userId: "renter-1",
    machineId: "pc-one",
    state: "active",
    revision: 1,
    startsAtUtc: "2026-11-08T02:00:00.000Z",
    expiresAtUtc: "2026-11-08T04:00:00.000Z",
    pendingRevision: null,
  },
  {
    id: "rental-2",
    userId: "renter-1",
    machineId: "pc-offline",
    state: "preparing",
    revision: 1,
    startsAtUtc: null,
    expiresAtUtc: null,
    pendingRevision: null,
  },
  {
    id: "rental-3",
    userId: "renter-1",
    machineId: "pc-unknown",
    state: "preparing",
    revision: 1,
    startsAtUtc: null,
    expiresAtUtc: null,
    pendingRevision: null,
  },
  {
    id: "rental-4",
    userId: "renter-1",
    machineId: "pc-unrecognized",
    state: "preparing",
    revision: 1,
    startsAtUtc: null,
    expiresAtUtc: null,
    pendingRevision: null,
  },
];

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

  it("maps Controller heartbeat state to each lease without exposing machine network data", async () => {
    const response = await GET(new Request("http://localhost/api/my-leases"));
    const data = await response.json();

    expect(controllerCall).toHaveBeenCalledWith(
      "/internal/users/renter-1/leases",
    );
    expect(controllerCall).toHaveBeenCalledWith("/internal/machines");
    expect(data.map(({ id, agent }: (typeof data)[number]) => ({ id, agent }))).toEqual([
      {
        id: "rental-1",
        agent: { reachability: "reachable", lastSeenAt: HEARTBEAT_AT },
      },
      {
        id: "rental-2",
        agent: {
          reachability: "unreachable",
          lastSeenAt: "2026-11-08T02:00:00.000Z",
        },
      },
      {
        id: "rental-3",
        agent: { reachability: "unknown", lastSeenAt: null },
      },
      {
        id: "rental-4",
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

    const response = await GET(new Request("http://localhost/api/my-leases"));
    const data = await response.json();

    expect(data[0].connection).toEqual({
      address: "203.0.113.7:47989",
      stale: true,
    });
    expect(data[0].machineDisplayName).toBe("DESKTOP-ONE");
    expect(data[1].connection).toEqual({ address: null, stale: false });
    expect(controllerCall).toHaveBeenCalledWith(
      "/internal/leases/rental-1/connection",
    );
    expect(
      controllerCall.mock.calls.some(([path]) =>
        String(path).includes("rental-2/connection"),
      ),
    ).toBe(false);
    expect(JSON.stringify(data)).not.toContain("must not reach the browser");
  });
});
