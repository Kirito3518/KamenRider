import { beforeEach, describe, expect, it, vi } from "vitest";

const h = vi.hoisted(() => {
  class MockControllerError extends Error {
    constructor(
      public status: number,
      public safeReleaseReason: string | null = null,
      public safeRecoveryHead: unknown = null,
    ) {
      super("Controller unavailable");
    }
  }
  return {
    controllerRequest: vi.fn(),
    ControllerError: MockControllerError,
    role: "admin" as string | null,
    actorId: "admin-1",
  };
});

vi.mock("@/lib/auth", () => ({
  getAuth: async () => ({
    api: {
      getSession: async () =>
        h.role ? { user: { id: h.actorId, role: h.role } } : null,
    },
  }),
}));

vi.mock("@/lib/controller-client", () => ({
  controllerRequest: h.controllerRequest,
  ControllerError: h.ControllerError,
}));

import {
  GET,
  PUT,
} from "@/app/api/admin/sunshine-credential/route";

const CREDENTIAL_STATE = {
  configured: true,
  userName: "sunshine-admin",
  revision: 6,
  updatedAtUtc: "2026-11-17T07:00:00.000Z",
};

function request(
  method: "GET" | "PUT",
  body?: unknown,
  headers: Record<string, string> = {},
) {
  return new Request("http://localhost/api/admin/sunshine-credential", {
    method,
    headers: {
      origin: "http://localhost",
      "content-type": "application/json",
      ...headers,
    },
    body: body === undefined ? undefined : JSON.stringify(body),
  });
}

function controllerHeaders(call: unknown[]) {
  return new Headers((call[1] as RequestInit | undefined)?.headers);
}

describe("admin Sunshine credential route", () => {
  beforeEach(() => {
    h.controllerRequest.mockReset();
    h.controllerRequest.mockResolvedValue(CREDENTIAL_STATE);
    h.role = "admin";
    h.actorId = "admin-1";
    process.env.BETTER_AUTH_URL = "http://localhost";
  });

  it("returns the Controller credential state to an Admin with no-store", async () => {
    const response = await GET(request("GET"));

    expect(response.status).toBe(200);
    expect(response.headers.get("cache-control")).toBe("no-store");
    expect(h.controllerRequest).toHaveBeenCalledTimes(1);
    const call = h.controllerRequest.mock.calls[0];
    expect(call[0]).toBe("/internal/sunshine-credential");
    expect(controllerHeaders(call).get("x-authenticated-actor-id")).toBe(
      "admin-1",
    );
    expect(controllerHeaders(call).get("x-authenticated-actor-role")).toBe(
      "admin",
    );
    expect(await response.json()).toEqual(CREDENTIAL_STATE);
  });

  it("requires an authenticated Admin session", async () => {
    h.role = null;
    const unauthenticated = await GET(request("GET"));
    expect(unauthenticated.status).toBe(401);
    expect(h.controllerRequest).not.toHaveBeenCalled();

    h.role = "user";
    h.controllerRequest.mockClear();
    const forbidden = await GET(request("GET"));
    expect(forbidden.status).toBe(403);
    expect(h.controllerRequest).not.toHaveBeenCalled();
  });

  it("forwards a trimmed username and the password only to the Controller", async () => {
    const response = await PUT(
      request("PUT", { userName: "  new-sunshine  ", password: "s3cret-value" }),
    );

    expect(response.status).toBe(200);
    expect(h.controllerRequest).toHaveBeenCalledTimes(1);
    const call = h.controllerRequest.mock.calls[0];
    expect(call[0]).toBe("/internal/sunshine-credential");
    expect((call[1] as RequestInit).method).toBe("PUT");
    expect(JSON.parse(String((call[1] as RequestInit).body))).toEqual({
      userName: "new-sunshine",
      password: "s3cret-value",
    });
    const body = await response.json();
    expect(body).toEqual(CREDENTIAL_STATE);
    expect(body.password).toBeUndefined();
    expect(JSON.stringify(body)).not.toContain("s3cret-value");
  });

  it("never returns a password even if the Controller leaks one", async () => {
    h.controllerRequest.mockResolvedValueOnce({
      ...CREDENTIAL_STATE,
      password: "leaked-controller-value",
    });

    const response = await PUT(
      request("PUT", { userName: "sunshine-admin", password: "s3cret-value" }),
    );

    expect(response.status).toBe(200);
    const raw = await response.text();
    expect(raw).not.toContain("leaked-controller-value");
    expect(raw).not.toContain("s3cret-value");
    expect(JSON.parse(raw)).toEqual(CREDENTIAL_STATE);
  });

  it("rejects malformed or unexpected request bodies without calling the Controller", async () => {
    for (const body of [
      {},
      { userName: "admin" },
      { userName: "", password: "s3cret-value" },
      { userName: "admin", password: "" },
      { userName: "admin", password: "s3cret-value", extra: true },
      { userName: "bad\u0000name", password: "s3cret-value" },
    ]) {
      const response = await PUT(request("PUT", body));
      expect(response.status).toBe(400);
    }
    expect(h.controllerRequest).not.toHaveBeenCalled();

    const malformed = await PUT(
      new Request("http://localhost/api/admin/sunshine-credential", {
        method: "PUT",
        headers: { origin: "http://localhost", "content-type": "application/json" },
        body: "{not-json",
      }),
    );
    expect(malformed.status).toBe(400);
    expect(h.controllerRequest).not.toHaveBeenCalled();
  });

  it("rejects a cross-origin update before calling the Controller", async () => {
    const response = await PUT(
      request(
        "PUT",
        { userName: "sunshine-admin", password: "s3cret-value" },
        { origin: "https://evil.example" },
      ),
    );
    expect(response.status).toBe(403);
    expect(h.controllerRequest).not.toHaveBeenCalled();
  });
});
