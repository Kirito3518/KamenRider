import { beforeEach, describe, expect, it, vi } from "vitest";

const { controllerRequest, ControllerError, verifyAssets, AssetsUnavailableError } = vi.hoisted(() => {
  class MockControllerError extends Error {
    constructor(public status: number, public safeReleaseReason: string | null = null,
      public safeRecoveryHead: { currentChainId: string; currentHeadOperationId: string; currentGeneration: number } | null = null) {
      super("Controller unavailable");
    }
  }
  class MockAssetsUnavailableError extends Error {
    constructor() { super("unavailable"); }
  }
  return {
    controllerRequest: vi.fn(),
    ControllerError: MockControllerError,
    verifyAssets: vi.fn(),
    AssetsUnavailableError: MockAssetsUnavailableError,
  };
});

vi.mock("@/lib/auth", () => ({
  getAuth: async () => ({
    api: { getSession: async () => ({ user: { id: "admin-1", role: "admin" } }) },
  }),
}));

vi.mock("@/lib/controller-client", () => ({ controllerRequest, ControllerError }));

vi.mock("@/lib/agent-maintenance-assets", () => ({
  AgentMaintenanceAssetsUnavailableError: AssetsUnavailableError,
  verifyDeploymentAgentMaintenanceAssets: verifyAssets,
}));

import { POST } from "@/app/api/admin/machines/[machineId]/agent-update-command/route";

const machineId = "0123456789abcdef0123456789abcdef";
const operationId = "fedcba9876543210fedcba9876543210";
const ticket = "T".repeat(43);
const expiresAtUtc = new Date(Date.now() + 10 * 60_000).toISOString();
const wrapperSha256 = "b".repeat(64);
const helperSha256 = "a".repeat(64);
const helperRelativeUrl = `/agent/maintenance/sha256/${helperSha256}/AgentMaintenance.exe`;

const release = {
  version: "2.3.4",
  packageSha256: "c".repeat(64),
  targetRuntime: "win-x64",
  agentUpdateChallengeProtocol: 2,
};

function reservation(overrides: Record<string, unknown> = {}) {
  return {
    operationId,
    machineId,
    ticket,
    expiresAtUtc,
    releaseVersion: "2.3.4",
    targetVersion: "2.3.4",
    targetPackageSha256: "c".repeat(64),
    chainId: "chain-1",
    currentHeadOperationId: operationId,
    generation: 1,
    keyFingerprint: "f".repeat(64),
    targetPackageLength: 123456,
    ...overrides,
  };
}

function invoke(body: unknown, options: { origin?: string | null } = {}) {
  const headers: Record<string, string> = { "content-type": "application/json" };
  if (options.origin !== null) headers.origin = options.origin ?? "http://localhost";
  return POST(
    new Request(`http://localhost/api/admin/machines/${machineId}/agent-update-command`, {
      method: "POST",
      headers,
      body: JSON.stringify(body),
    }),
    { params: Promise.resolve({ machineId }) },
  );
}

function enableDeployment() {
  process.env.AGENT_UPDATE_ENABLED = "true";
  process.env.AGENT_CONTROLLER_URL = "https://controller.example:7443";
  process.env.AGENT_INSTALL_BASE_URL = "https://assets.example";
  process.env.AGENT_TRUST_ROOT_THUMBPRINT = "D".repeat(40);
}

function mockAssets() {
  verifyAssets.mockResolvedValue({
    helperProtocol: 1,
    helper: { relativeUrl: helperRelativeUrl, length: 123_456, sha256: helperSha256 },
    wrapper: { relativeUrl: "/agent/Update-Agent.ps1", length: 2048, sha256: wrapperSha256 },
  });
}

describe("POST /api/admin/machines/[machineId]/agent-update-command", () => {
  beforeEach(() => {
    controllerRequest.mockReset();
    verifyAssets.mockReset();
    process.env.BETTER_AUTH_URL = "http://localhost";
    delete process.env.AGENT_UPDATE_ENABLED;
    delete process.env.AGENT_CONTROLLER_URL;
    delete process.env.AGENT_INSTALL_BASE_URL;
    delete process.env.AGENT_TRUST_ROOT_THUMBPRINT;
  });

  it("stays unavailable unless the deployment explicitly enables update commands", async () => {
    const response = await invoke({ releaseVersion: "2.3.4" });

    expect(response.status).toBe(200);
    expect(response.headers.get("cache-control")).toBe("no-store");
    expect((await response.json()).available).toBe(false);
    expect(controllerRequest).not.toHaveBeenCalled();
    expect(verifyAssets).not.toHaveBeenCalled();
  });

  it("verifies pinned deployment assets and forwards one reservation before composing the command", async () => {
    enableDeployment();
    mockAssets();
    controllerRequest.mockResolvedValueOnce([release]).mockResolvedValueOnce(reservation());

    const response = await invoke({ releaseVersion: "2.3.4", requestId: "update-request-1" });
    const body = await response.json();

    expect(response.status).toBe(200);
    expect(response.headers.get("cache-control")).toBe("no-store");
    expect(verifyAssets).toHaveBeenCalledWith("win-x64");
    expect(controllerRequest).toHaveBeenNthCalledWith(1, "/internal/agent-releases", expect.anything());
    const reservationCall = controllerRequest.mock.calls[1];
    expect(reservationCall[0]).toBe(`/internal/machines/${machineId}/agent-updates`);
    expect(reservationCall[1].method).toBe("POST");
    expect(JSON.parse(reservationCall[1].body)).toEqual({ releaseVersion: "2.3.4", requestId: "update-request-1" });
    expect(body.available).toBe(true);
    expect(body.operationId).toBe(operationId);
    expect(body.chainId).toBe("chain-1");
    expect(body.currentHeadOperationId).toBe(operationId);
    expect(body.generation).toBe(1);
    expect(body.keyFingerprint).toBe("F".repeat(64));
    expect(body.targetPackageLength).toBe(123456);
    expect(body.helperRelativeUrl).toBe(helperRelativeUrl);
    expect(body.wrapperSha256).toBe(wrapperSha256.toUpperCase());
    expect(body.command).toContain(`-UpdateTicket '${ticket}'`);
    expect(body.command).toContain(`-HelperSha256 '${helperSha256.toUpperCase()}'`);
    expect(body.command).toContain(`-WrapperLength 2048`);
    expect(body.command).toContain(`-TargetRuntime 'win-x64'`);
    expect(body.command).toContain(`-EnrollmentKeyFingerprint '${"F".repeat(64)}'`);
    expect(body.command).toContain(`-Generation 1`);
    expect(body.command).toContain(`-PackageLength 123456`);
    expect(body.command).toContain(`-TrustedRootThumbprint '${"D".repeat(40)}'`);
    expect(body.command).toContain("https://assets.example/agent/Update-Agent.ps1");
  });

  it("returns an unavailable command object when no capable release is published", async () => {
    enableDeployment();
    mockAssets();
    controllerRequest.mockResolvedValueOnce([{ ...release, agentUpdateChallengeProtocol: 1 }]);

    const response = await invoke({ releaseVersion: "2.3.4" });

    expect(response.status).toBe(200);
    expect((await response.json()).available).toBe(false);
    expect(verifyAssets).not.toHaveBeenCalled();
  });

  it("returns an unavailable command object when the pinned maintenance assets are missing", async () => {
    enableDeployment();
    controllerRequest.mockResolvedValueOnce([release]);
    verifyAssets.mockRejectedValueOnce(new AssetsUnavailableError());

    const response = await invoke({ releaseVersion: "2.3.4" });

    expect(response.status).toBe(200);
    expect((await response.json()).available).toBe(false);
    expect(controllerRequest).toHaveBeenCalledTimes(1);
  });

  it("rejects malformed bodies and foreign origins before contacting the Controller", async () => {
    enableDeployment();
    const invalid = await invoke({ releaseVersion: "2.3.4", extra: true });
    const foreignOrigin = await invoke({ releaseVersion: "2.3.4" }, { origin: "http://evil.example" });

    expect(invalid.status).toBe(400);
    expect(foreignOrigin.status).toBe(403);
    expect(controllerRequest).not.toHaveBeenCalled();
  });

  it("maps a Controller reservation conflict without issuing a command", async () => {
    enableDeployment();
    mockAssets();
    const conflict = new ControllerError(409, null, {
      currentChainId: "other-chain",
      currentHeadOperationId: operationId,
      currentGeneration: 2,
    });
    controllerRequest.mockResolvedValueOnce([release]).mockRejectedValueOnce(conflict);

    const response = await invoke({ releaseVersion: "2.3.4" });

    expect(response.status).toBe(409);
    expect(await response.json()).toEqual({
      currentChainId: "other-chain",
      currentHeadOperationId: operationId,
      currentGeneration: 2,
    });
  });
});
