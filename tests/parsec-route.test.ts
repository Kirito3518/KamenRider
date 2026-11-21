import { beforeEach, describe, expect, it, vi } from "vitest";

type MockCall = (path: string, init?: RequestInit) => Promise<unknown>;
type MockAction = (
  actor: { id: string; role: string },
  call: MockCall,
) => Promise<unknown>;

const { controllerCall } = vi.hoisted(() => ({ controllerCall: vi.fn() }));

vi.mock("@/lib/machine-api", () => ({
  segment: (value: string) => encodeURIComponent(value),
  text: (value: unknown) => {
    if (typeof value !== "string" || !value.trim() || value.length > 128)
      throw new TypeError("Invalid text");
    return value;
  },
  machineApi: async (
    _request: Request,
    _admin: boolean,
    action: MockAction,
  ) => {
    try {
      const result = await action(
        { id: "renter-1", role: "user" },
        controllerCall as MockCall,
      );
      return Response.json(result);
    } catch (error) {
      if (error instanceof TypeError)
        return Response.json({ error: "Invalid request" }, { status: 400 });
      throw error;
    }
  },
}));

import { POST } from "@/app/api/my-leases/[rentalId]/parsec/route";

const SECRET = {
  wrappedKey: "wrapped-key",
  iv: "initialization-vector",
  ciphertext: "ciphertext",
};

function requestBody(extra: Record<string, unknown> = {}) {
  return { idempotencyKey: "idem-1", secret: SECRET, ...extra };
}

function post(body: Record<string, unknown>) {
  return POST(
    new Request("http://localhost/api/my-leases/rental-1/parsec", {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify(body),
    }),
    { params: Promise.resolve({ rentalId: "rental-1" }) },
  );
}

function sentBody() {
  expect(controllerCall).toHaveBeenCalledTimes(1);
  const [path, init] = controllerCall.mock.calls[0] as [string, RequestInit];
  expect(path).toBe("/internal/leases/rental-1/parsec");
  expect(init.method).toBe("POST");
  return JSON.parse(String(init.body)) as Record<string, unknown>;
}

describe("POST /api/my-leases/[rentalId]/parsec", () => {
  beforeEach(() => {
    controllerCall.mockReset();
    controllerCall.mockResolvedValue({ attemptId: "attempt-1", state: "queued" });
  });

  it("passes retryAttemptId through to the Controller", async () => {
    const retryAttemptId = "a".repeat(32);

    const response = await post(requestBody({ retryAttemptId }));

    expect(response.status).toBe(200);
    expect(sentBody()).toEqual({
      userId: "renter-1",
      idempotencyKey: "idem-1",
      retryAttemptId,
      secret: SECRET,
    });
  });

  it("omits retryAttemptId when the request does not include it", async () => {
    const response = await post(requestBody());

    expect(response.status).toBe(200);
    const body = sentBody();
    expect("retryAttemptId" in body).toBe(false);
    expect(body).toEqual({
      userId: "renter-1",
      idempotencyKey: "idem-1",
      secret: SECRET,
    });
  });

  it("treats a null retryAttemptId as absent", async () => {
    const response = await post(requestBody({ retryAttemptId: null }));

    expect(response.status).toBe(200);
    expect("retryAttemptId" in sentBody()).toBe(false);
  });

  it("accepts the Controller's 128-character identifier limit", async () => {
    const retryAttemptId = "b".repeat(128);

    const response = await post(requestBody({ retryAttemptId }));

    expect(response.status).toBe(200);
    expect(sentBody().retryAttemptId).toBe(retryAttemptId);
  });

  it.each([
    ["a number", 123],
    ["an empty string", ""],
    ["whitespace only", "   "],
    ["an oversized string", "c".repeat(129)],
    ["an object", {}],
    ["an array", []],
  ])("rejects retryAttemptId that is %s", async (_label, retryAttemptId) => {
    const response = await post(requestBody({ retryAttemptId }));

    expect(response.status).toBe(400);
    expect(await response.json()).toEqual({ error: "Invalid request" });
    expect(controllerCall).not.toHaveBeenCalled();
  });
});
