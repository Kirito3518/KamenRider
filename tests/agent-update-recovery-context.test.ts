import { beforeEach, describe, expect, it, vi } from "vitest";

const { controllerRequest, getSession, ControllerError } = vi.hoisted(() => {
  class MockControllerError extends Error {
    constructor(public status: number) {
      super("Controller unavailable");
    }
  }
  return {
    controllerRequest: vi.fn(),
    getSession: vi.fn(),
    ControllerError: MockControllerError,
  };
});

vi.mock("@/lib/auth", () => ({
  getAuth: async () => ({ api: { getSession } }),
}));
vi.mock("@/lib/controller-client", () => ({
  controllerRequest,
  ControllerError,
}));

import { GET } from "@/app/api/admin/machines/[machineId]/agent-update-recovery-context/route";
import { GET as getPublicMachines } from "@/app/api/machines/route";

const adminSession = { user: { id: "trusted-admin", role: "admin" } };
const contextDto = {
  machineId: "pc-1",
  chainId: "chain-1",
  currentHeadOperationId: "operation-4",
  currentGeneration: 4,
  currentStatus: "failed",
  currentTargetVersion: "2.0.0",
  held: true,
  allocationHeld: false,
};

function getRequest(machineId = "pc-1") {
  return new Request(
    `https://admin.example/api/admin/machines/${encodeURIComponent(machineId)}/agent-update-recovery-context`,
  );
}

describe("admin recovery context proxy", () => {
  beforeEach(() => {
    controllerRequest.mockReset();
    getSession.mockReset();
    getSession.mockResolvedValue(adminSession);
    process.env.BETTER_AUTH_URL = "https://admin.example";
  });

  it("forwards a trusted admin to the private Controller route and returns only validated held context", async () => {
    controllerRequest.mockResolvedValue({
      ...contextDto,
      targetPackageSha256: "private-package-hash",
      backupDigest: "private-backup-digest",
    });
    const response = await GET(getRequest("pc-1"), {
      params: Promise.resolve({ machineId: "pc-1" }),
    });

    expect(response.status).toBe(200);
    expect(response.headers.get("cache-control")).toBe("no-store");
    expect(controllerRequest).toHaveBeenCalledTimes(1);
    expect(controllerRequest.mock.calls[0]?.[0]).toBe(
      "/internal/machines/pc-1/agent-update-recovery-context",
    );
    const init = controllerRequest.mock.calls[0]?.[1] as RequestInit;
    expect(new Headers(init.headers).get("X-Authenticated-Actor-Id")).toBe("trusted-admin");
    expect(new Headers(init.headers).get("X-Authenticated-Actor-Role")).toBe("admin");
    const body = await response.json();
    expect(body).toEqual({ available: true, context: contextDto });
    expect(JSON.stringify(body)).not.toContain("private-package-hash");
    expect(JSON.stringify(body)).not.toContain("private-backup-digest");
  });

  it("rejects unauthenticated and non-admin sessions before contacting Controller", async () => {
    getSession.mockResolvedValueOnce(null);
    const unauthenticated = await GET(getRequest(), {
      params: Promise.resolve({ machineId: "pc-1" }),
    });
    expect(unauthenticated.status).toBe(401);

    getSession.mockResolvedValueOnce({ user: { id: "ordinary-user", role: "user" } });
    const nonAdmin = await GET(getRequest(), {
      params: Promise.resolve({ machineId: "pc-1" }),
    });
    expect(nonAdmin.status).toBe(403);
    expect(controllerRequest).not.toHaveBeenCalled();
  });

  it("rejects a Controller context that is not held or is for another machine", async () => {
    controllerRequest.mockResolvedValueOnce({ ...contextDto, held: false });
    const notHeld = await GET(getRequest(), {
      params: Promise.resolve({ machineId: "pc-1" }),
    });
    expect(notHeld.status).toBe(503);
    expect(await notHeld.json()).toMatchObject({ available: false });

    controllerRequest.mockResolvedValueOnce({ ...contextDto, machineId: "pc-2" });
    const mismatch = await GET(getRequest(), {
      params: Promise.resolve({ machineId: "pc-1" }),
    });
    expect(mismatch.status).toBe(503);
    expect(await mismatch.json()).toMatchObject({ available: false });
  });

  it("fails closed while recovery context is quarantined or unavailable", async () => {
    controllerRequest.mockRejectedValue(new ControllerError(503));
    const response = await GET(getRequest(), {
      params: Promise.resolve({ machineId: "pc-1" }),
    });

    expect(response.status).toBe(503);
    expect(await response.json()).toEqual({
      available: false,
      error: "Recovery context is unavailable while the Controller is quarantined or recovery is disabled.",
    });
  });

  it("does not expose recovery context or internal operation identifiers on the public machine API", async () => {
    controllerRequest.mockResolvedValueOnce([{
      machineId: "pc-1", approvalStatus: "approved", displayName: "PC 1",
      hourlyPriceMinorUnits: 100, canRent: true, readiness: "ready",
      chainId: "private-chain", currentHeadOperationId: "private-head",
      agentUpdateOperationId: "private-operation", targetPackageSha256: "private-hash",
    }]);
    const response = await getPublicMachines(new Request("https://admin.example/api/machines"));

    expect(response.status).toBe(200);
    const body = await response.json();
    expect(body).toMatchObject([{ id: "pc-1", name: "PC 1", priceSatang: 100, status: "available" }]);
    expect(Object.keys(body[0]).sort()).toEqual(["cpu", "game", "gpu", "id", "name", "priceSatang", "ram", "ramBytes", "status", "storage"].sort());
    expect(JSON.stringify(body)).not.toContain("private-chain");
    expect(JSON.stringify(body)).not.toContain("private-head");
    expect(JSON.stringify(body)).not.toContain("private-operation");
    expect(JSON.stringify(body)).not.toContain("private-hash");
  });
});
