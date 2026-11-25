import { randomBytes } from "node:crypto";
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

/** A real RSA-2048 OAEP ciphertext is 256 bytes => 342 base64url characters. */
const WRAPPED_KEY_LENGTH = 342;

function base64Url(bytes: number) {
  return randomBytes(bytes).toString("base64url");
}

/**
 * A browser-shaped sealed secret. The previous fixture used short words for every field, which
 * hid the bug: the generic `text()` validator rejected anything longer than 128 characters, and
 * every real wrapped key is longer than that.
 */
const SECRET = {
  wrappedKey: base64Url(256),
  iv: base64Url(12),
  ciphertext: base64Url(96),
};

function requestBody(extra: Record<string, unknown> = {}) {
  return { idempotencyKey: "idem-1", secret: SECRET, ...extra };
}

function postBody(body: unknown) {
  return POST(
    new Request("http://localhost/api/my-leases/rental-1/parsec", {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify(body),
    }),
    { params: Promise.resolve({ rentalId: "rental-1" }) },
  );
}

function post(body: Record<string, unknown>) {
  return postBody(body);
}

function sentBody() {
  expect(controllerCall).toHaveBeenCalledTimes(1);
  const [path, init] = controllerCall.mock.calls[0] as [string, RequestInit];
  expect(path).toBe("/internal/leases/rental-1/parsec");
  expect(init.method).toBe("POST");
  return JSON.parse(String(init.body)) as Record<string, unknown>;
}

function secretBody(overrides: Record<string, unknown>) {
  return requestBody({ secret: { ...SECRET, ...overrides } });
}

describe("POST /api/my-leases/[rentalId]/parsec", () => {
  beforeEach(() => {
    controllerCall.mockReset();
    controllerCall.mockResolvedValue({ attemptId: "attempt-1", state: "queued" });
  });

  it("forwards a real browser-sealed secret and returns the Controller attempt", async () => {
    expect(SECRET.wrappedKey).toHaveLength(WRAPPED_KEY_LENGTH);
    expect(SECRET.wrappedKey.length).toBeGreaterThan(128);

    const response = await post(requestBody());

    expect(response.status).toBe(200);
    expect(await response.json()).toEqual({
      attemptId: "attempt-1",
      state: "queued",
    });
    expect(sentBody()).toEqual({
      userId: "renter-1",
      idempotencyKey: "idem-1",
      secret: SECRET,
    });
  });

  it("passes retryAttemptId through to the Controller", async () => {
    const retryAttemptId = "a".repeat(32);

    const response = await post(requestBody({ retryAttemptId }));

    expect(response.status).toBe(200);
    const body = sentBody();
    expect(body.retryAttemptId).toBe(retryAttemptId);
    expect(body.secret).toEqual(SECRET);
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

  it.each<[string, PutSecret]>([
    ["wrappedKey with a non-base64url character", { key: "wrappedKey", value: `${base64Url(256)}+` }],
    ["wrappedKey with whitespace", { key: "wrappedKey", value: `${base64Url(256)} ` }],
    ["wrappedKey over 512 characters", { key: "wrappedKey", value: "a".repeat(513) }],
    ["wrappedKey with padding", { key: "wrappedKey", value: `${base64Url(256)}=` }],
    ["iv with a non-base64url character", { key: "iv", value: "abc.def" }],
    ["iv over 64 characters", { key: "iv", value: "a".repeat(65) }],
    ["ciphertext with a non-base64url character", { key: "ciphertext", value: "abc/def" }],
    ["ciphertext over 8192 characters", { key: "ciphertext", value: "a".repeat(8193) }],
    ["an empty wrappedKey", { key: "wrappedKey", value: "" }],
    ["a numeric iv", { key: "iv", value: 12 }],
    ["a missing ciphertext", { key: "ciphertext", value: undefined }],
  ])("rejects a sealed secret with %s", async (_label, { key, value }) => {
    const response = await post(secretBody({ [key]: value }));

    expect(response.status).toBe(400);
    expect(await response.json()).toEqual({ error: "Invalid request" });
    expect(controllerCall).not.toHaveBeenCalled();
  });

  it.each([
    ["a null body", null],
    ["an array body", []],
    ["a string body", "idem-1"],
    ["a numeric body", 1],
    ["a body without a secret", { idempotencyKey: "idem-1" }],
    ["a secret that is not an object", { idempotencyKey: "idem-1", secret: "secret" }],
  ])("rejects %s", async (_label, body) => {
    const response = await postBody(body);

    expect(response.status).toBe(400);
    expect(await response.json()).toEqual({ error: "Invalid request" });
    expect(controllerCall).not.toHaveBeenCalled();
  });
});

type PutSecret = { key: "wrappedKey" | "iv" | "ciphertext"; value: unknown };
