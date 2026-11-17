import { beforeEach, describe, expect, it, vi } from "vitest";

const h = vi.hoisted(() => ({
  calls: [] as string[],
  machines: [] as unknown[],
}));

type MockAction = (
  actor: { id: string; role: string },
  call: (path: string) => Promise<unknown>,
) => Promise<unknown>;

vi.mock("@/lib/machine-api", () => ({
  machineApi: async (
    _request: Request,
    _admin: boolean,
    action: MockAction,
  ) =>
    Response.json(
      await action({ id: "", role: "user" }, async (path: string) => {
        h.calls.push(path);
        if (path !== "/internal/machines")
          throw new Error(`Unexpected Controller request: ${path}`);
        return h.machines;
      }),
    ),
}));

import { GET } from "@/app/api/machines/route";

function controllerMachine(overrides: Record<string, unknown>) {
  return {
    machineId: "pc-1",
    approvalStatus: "approved",
    displayName: "DESKTOP-ONE",
    hostname: "DESKTOP-ONE",
    cpu: "AMD Ryzen 7 7800X3D",
    gpu: "NVIDIA GeForce RTX 4080",
    ramBytes: 34_359_738_368,
    appliedSunshineCredentialRevision: 3,
    hourlyPriceMinorUnits: 4_000,
    localIp: "192.168.1.10",
    vncLocalIp: "192.168.1.11",
    macAddress: "001122334455",
    moonlightPort: 47989,
    sunshinePort: 47990,
    configRevision: 2,
    appliedConfigRevision: 2,
    readiness: "ready",
    canRent: true,
    publicKeyFingerprint: "secret-management-fingerprint",
    lastSeenAt: "2026-11-17T06:00:00.000Z",
    agentVersion: "1.0.0",
    activeAdminVncSessions: 0,
    ...overrides,
  };
}

describe("GET /api/machines customer machine list", () => {
  beforeEach(() => {
    h.calls = [];
    h.machines = [];
  });

  it("exposes specs for approved rentable machines and nothing private", async () => {
    h.machines = [
      controllerMachine({}),
      controllerMachine({ machineId: "pending-pc", approvalStatus: "pending" }),
      controllerMachine({ machineId: "held-pc", canRent: false }),
    ];

    const response = await GET(new Request("http://localhost/api/machines"));
    const data = await response.json();

    expect(h.calls).toEqual(["/internal/machines"]);
    expect(data).toHaveLength(1);
    expect(data[0]).toEqual({
      id: "pc-1",
      name: "DESKTOP-ONE",
      price: 40,
      status: "available",
      cpu: "AMD Ryzen 7 7800X3D",
      gpu: "NVIDIA GeForce RTX 4080",
      ram: 32,
      ramBytes: 34_359_738_368,
      storage: "—",
      game: "",
    });

    const raw = JSON.stringify(data);
    for (const privateValue of [
      "192.168.1.10",
      "192.168.1.11",
      "001122334455",
      "47989",
      "47990",
      "secret-management-fingerprint",
    ])
      expect(raw).not.toContain(privateValue);
  });

  it("uses neutral placeholders when the Agent has not reported hardware", async () => {
    h.machines = [
      controllerMachine({ cpu: null, gpu: null, ramBytes: 0, hostname: null }),
    ];

    const data = await (
      await GET(new Request("http://localhost/api/machines"))
    ).json();

    expect(data[0]).toMatchObject({
      cpu: "—",
      gpu: "—",
      ram: 0,
      ramBytes: 0,
    });
  });
});
