import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";

const { controllerRequest, getSession, ControllerError } = vi.hoisted(() => {
  class MockControllerError extends Error {
    constructor(
      public status: number,
      public safeReleaseReason: string | null = null,
      public safeRecoveryHead: {
        currentChainId: string;
        currentHeadOperationId: string;
        currentGeneration: number;
      } | null = null,
    ) {
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

import {
  GET,
  POST,
} from "@/app/api/admin/machines/[machineId]/agent-update-recovery-intents/route";

const adminSession = { user: { id: "trusted-admin", role: "admin" } };
const createBody = {
  expectedChainId: "chain-1",
  expectedHeadId: "operation-4",
  expectedGeneration: 4,
  targetReleaseVersion: "2.3.4",
  reason: "Inspect the held update after a failed health check.",
  requestId: "retry-1",
};
const intentDto = {
  intentId: "intent-1",
  machineId: "machine-1",
  chainId: "chain-1",
  previousHeadOperationId: "operation-4",
  expectedGeneration: 4,
  generation: 5,
  targetReleaseVersion: "2.3.4",
  targetVersion: "2.3.4",
  targetPackageSha256: "private-package-hash",
  targetRuntime: "win-x64",
  reason: createBody.reason,
  actorId: "trusted-admin",
  requestId: "retry-1",
  status: "awaitingInspection",
  held: true,
  createdAtUtc: "2026-11-12T00:00:00Z",
  expiresAtUtc: "2099-01-01T00:00:00Z",
};

function postRequest(machineId = "machine-1", body: unknown = createBody) {
  return new Request(
    `https://admin.example/api/admin/machines/${encodeURIComponent(machineId)}/agent-update-recovery-intents`,
    {
      method: "POST",
      headers: {
        origin: "https://admin.example",
        "content-type": "application/json",
        "x-authenticated-actor-id": "forged-user",
        "x-authenticated-actor-role": "admin",
      },
      body: JSON.stringify(body),
    },
  );
}

describe("recovery intent admin proxy", () => {
  afterEach(() => {
    vi.useRealTimers();
  });

  beforeEach(() => {
    controllerRequest.mockReset();
    getSession.mockReset();
    getSession.mockResolvedValue(adminSession);
    process.env.BETTER_AUTH_URL = "https://admin.example";
  });

  it("posts an encoded machine path using the authenticated admin and returns a held pending intent without package hashes", async () => {
    controllerRequest.mockResolvedValue({ ...intentDto, machineId: "machine/one" });
    const response = await POST(postRequest("machine/one"), {
      params: Promise.resolve({ machineId: "machine/one" }),
    });

    expect(response.status).toBe(200);
    expect(response.headers.get("cache-control")).toBe("no-store");
    expect(controllerRequest).toHaveBeenCalledTimes(1);
    const [path, init] = controllerRequest.mock.calls[0] as [
      string,
      RequestInit,
    ];
    expect(path).toBe(
      "/internal/machines/machine%2Fone/agent-update-recovery-intents",
    );
    expect(init.method).toBe("POST");
    expect(JSON.parse(String(init.body))).toEqual(createBody);
    expect(JSON.stringify(init.body)).not.toContain("targetPackageSha256");
    expect(new Headers(init.headers).get("X-Authenticated-Actor-Id")).toBe(
      "trusted-admin",
    );
    expect(new Headers(init.headers).get("X-Authenticated-Actor-Role")).toBe(
      "admin",
    );
    expect(new Headers(init.headers).get("X-Authenticated-Actor-Id")).not.toBe(
      "forged-user",
    );
    expect(await response.json()).toEqual({
      available: true,
      pending: true,
      intent: {
        intentId: intentDto.intentId,
        machineId: "machine/one",
        chainId: intentDto.chainId,
        previousHeadOperationId: intentDto.previousHeadOperationId,
        expectedGeneration: intentDto.expectedGeneration,
        generation: intentDto.generation,
        targetReleaseVersion: intentDto.targetReleaseVersion,
        targetVersion: intentDto.targetVersion,
        targetRuntime: intentDto.targetRuntime,
        reason: intentDto.reason,
        requestId: intentDto.requestId,
        status: intentDto.status,
        held: intentDto.held,
        createdAtUtc: intentDto.createdAtUtc,
        expiresAtUtc: intentDto.expiresAtUtc,
      },
    });
  });

  it("rejects a package hash in the request body before contacting Controller", async () => {
    const response = await POST(
      postRequest("machine-1", {
        ...createBody,
        targetPackageSha256: "caller-controlled-hash",
      }),
      { params: Promise.resolve({ machineId: "machine-1" }) },
    );

    expect(response.status).toBe(400);
    expect(await response.json()).toEqual({ error: "Invalid request" });
    expect(controllerRequest).not.toHaveBeenCalled();
  });

  it("rejects browser-supplied actor and package URL fields before contacting Controller", async () => {
    const response = await POST(
      postRequest("machine-1", {
        ...createBody,
        actorId: "browser-admin",
        packageUrl: "https://attacker.example/update.zip",
      }),
      { params: Promise.resolve({ machineId: "machine-1" }) },
    );

    expect(response.status).toBe(400);
    expect(await response.json()).toEqual({ error: "Invalid request" });
    expect(controllerRequest).not.toHaveBeenCalled();
  });

  it("rejects control characters in recovery identifiers before contacting Controller", async () => {
    const response = await POST(
      postRequest("machine-1", {
        ...createBody,
        expectedChainId: "chain-1\u0001",
      }),
      { params: Promise.resolve({ machineId: "machine-1" }) },
    );

    expect(response.status).toBe(400);
    expect(await response.json()).toEqual({ error: "Invalid request" });
    expect(controllerRequest).not.toHaveBeenCalled();
  });

  it("rejects an oversized JSON body before contacting Controller", async () => {
    const request = new Request(
      "https://admin.example/api/admin/machines/machine-1/agent-update-recovery-intents",
      {
        method: "POST",
        headers: {
          origin: "https://admin.example",
          "content-type": "application/json",
        },
        body: `${JSON.stringify(createBody)}${" ".repeat(10_000)}`,
      },
    );
    const response = await POST(request, {
      params: Promise.resolve({ machineId: "machine-1" }),
    });

    expect(response.status).toBe(400);
    expect(await response.json()).toEqual({ error: "Invalid request" });
    expect(controllerRequest).not.toHaveBeenCalled();
  });

  it("does not report a pending intent bound to another chain", async () => {
    controllerRequest.mockResolvedValue({ ...intentDto, chainId: "chain-other" });
    const response = await POST(postRequest(), {
      params: Promise.resolve({ machineId: "machine-1" }),
    });

    expect(response.status).toBe(503);
    expect(await response.json()).toEqual({
      available: false,
      pending: false,
      error: "Recovery intents are not available right now.",
    });
  });

  it("does not report an intent returned for another authenticated actor", async () => {
    controllerRequest.mockResolvedValue({ ...intentDto, actorId: "other-admin" });
    const response = await POST(postRequest(), {
      params: Promise.resolve({ machineId: "machine-1" }),
    });

    expect(response.status).toBe(503);
    expect(await response.json()).toEqual({
      available: false,
      pending: false,
      error: "Recovery intents are not available right now.",
    });
  });

  it("accepts recovery reasons up to the Controller limit", async () => {
    const reason = "a".repeat(256);
    controllerRequest.mockResolvedValue({ ...intentDto, reason });
    const response = await POST(
      postRequest("machine-1", { ...createBody, reason }),
      { params: Promise.resolve({ machineId: "machine-1" }) },
    );

    expect(response.status).toBe(200);
    expect(await response.json()).toMatchObject({
      available: true,
      pending: true,
      intent: { reason },
    });
  });

  it("returns the actual current head on a stale 409 without losing no-store", async () => {
    const currentHead = {
      currentChainId: "chain-current",
      currentHeadOperationId: "operation-current",
      currentGeneration: 9,
    };
    controllerRequest.mockRejectedValue(new ControllerError(409, null, currentHead));
    const response = await POST(postRequest(), {
      params: Promise.resolve({ machineId: "machine-1" }),
    });

    expect(response.status).toBe(409);
    expect(response.headers.get("cache-control")).toBe("no-store");
    expect(await response.json()).toEqual({
      available: false,
      pending: false,
      error: "The maintenance head changed. Refresh the current state before retrying.",
      currentHead,
    });
  });

  it("allows an identical idempotent retry to return the same pending intent", async () => {
    controllerRequest.mockResolvedValue(intentDto);
    const first = await POST(postRequest(), {
      params: Promise.resolve({ machineId: "machine-1" }),
    });
    const retry = await POST(postRequest(), {
      params: Promise.resolve({ machineId: "machine-1" }),
    });

    expect(first.status).toBe(200);
    expect(retry.status).toBe(200);
    expect(controllerRequest).toHaveBeenCalledTimes(2);
    expect(controllerRequest.mock.calls[0]).toEqual(
      controllerRequest.mock.calls[1],
    );
    expect(await first.json()).toMatchObject({
      available: true,
      pending: true,
      intent: { intentId: "intent-1", requestId: "retry-1" },
    });
    expect(await retry.json()).toMatchObject({
      available: true,
      pending: true,
      intent: { intentId: "intent-1", requestId: "retry-1" },
    });
  });

  it("does not report an expired exact POST replay as pending or release its hold", async () => {
    vi.useFakeTimers();
    vi.setSystemTime(new Date("2026-11-13T00:00:00Z"));
    const expiredIntent = {
      ...intentDto,
      expiresAtUtc: "2026-11-12T23:59:59Z",
    };
    controllerRequest.mockResolvedValue(expiredIntent);

    const response = await POST(postRequest(), {
      params: Promise.resolve({ machineId: "machine-1" }),
    });

    expect(response.status).toBe(200);
    expect(await response.json()).toMatchObject({
      available: true,
      pending: false,
      intent: {
        held: true,
        status: "awaitingInspection",
        expiresAtUtc: expiredIntent.expiresAtUtc,
      },
    });
  });

  it("reconciles by an encoded request ID using the authenticated actor", async () => {
    controllerRequest.mockResolvedValue({ ...intentDto, requestId: "retry/one two" });
    const response = await GET(
      new Request(
        "https://admin.example/api/admin/machines/machine-1/agent-update-recovery-intents?requestId=retry%2Fone%20two",
      ),
      { params: Promise.resolve({ machineId: "machine-1" }) },
    );

    expect(response.status).toBe(200);
    expect(controllerRequest).toHaveBeenCalledWith(
      "/internal/machines/machine-1/agent-update-recovery-intents?requestId=retry%2Fone+two",
      expect.objectContaining({
        headers: expect.any(Headers),
      }),
    );
    const headers = new Headers(controllerRequest.mock.calls[0][1].headers);
    expect(headers.get("X-Authenticated-Actor-Id")).toBe("trusted-admin");
  });

  it("does not report an expired reconciled intent as pending or release its hold", async () => {
    vi.useFakeTimers();
    vi.setSystemTime(new Date("2026-11-13T00:00:00Z"));
    const expiredIntent = {
      ...intentDto,
      expiresAtUtc: "2026-11-12T23:59:59Z",
    };
    controllerRequest.mockResolvedValue(expiredIntent);

    const response = await GET(
      new Request(
        "https://admin.example/api/admin/machines/machine-1/agent-update-recovery-intents?requestId=retry-1",
      ),
      { params: Promise.resolve({ machineId: "machine-1" }) },
    );

    expect(response.status).toBe(200);
    expect(await response.json()).toMatchObject({
      available: true,
      pending: false,
      intent: {
        held: true,
        status: "awaitingInspection",
        expiresAtUtc: expiredIntent.expiresAtUtc,
      },
    });
  });

  it("does not reconcile an intent returned for another machine", async () => {
    controllerRequest.mockResolvedValue({ ...intentDto, machineId: "machine-other" });
    const response = await GET(
      new Request(
        "https://admin.example/api/admin/machines/machine-1/agent-update-recovery-intents?requestId=retry-1",
      ),
      { params: Promise.resolve({ machineId: "machine-1" }) },
    );

    expect(response.status).toBe(503);
    expect(await response.json()).toEqual({
      available: false,
      pending: false,
      error: "Recovery intents are not available right now.",
    });
  });

  it("reports the feature as unavailable instead of claiming an intent is pending", async () => {
    controllerRequest.mockRejectedValue(new ControllerError(503));
    const response = await POST(postRequest(), {
      params: Promise.resolve({ machineId: "machine-1" }),
    });

    expect(response.status).toBe(503);
    expect(await response.json()).toEqual({
      available: false,
      pending: false,
      error: "Recovery intents are not available right now.",
    });
  });

  it("does not proxy a forged or non-admin session", async () => {
    getSession.mockResolvedValue({ user: { id: "ordinary-user", role: "user" } });
    const response = await POST(postRequest(), {
      params: Promise.resolve({ machineId: "machine-1" }),
    });

    expect(response.status).toBe(403);
    expect(controllerRequest).not.toHaveBeenCalled();
  });

  it("does not proxy a cross-origin create request", async () => {
    const request = postRequest();
    const crossOrigin = new Request(request.url, {
      method: "POST",
      headers: {
        origin: "https://attacker.example",
        "content-type": "application/json",
      },
      body: JSON.stringify(createBody),
    });
    const response = await POST(crossOrigin, {
      params: Promise.resolve({ machineId: "machine-1" }),
    });

    expect(response.status).toBe(403);
    expect(controllerRequest).not.toHaveBeenCalled();
  });
});
