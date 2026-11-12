import { beforeEach, describe, expect, it, vi } from "vitest";

const { controllerRequest, ControllerError } = vi.hoisted(() => {
  class MockControllerError extends Error {
    constructor(public status: number, public safeReleaseReason: string | null = null) {
      super("Controller unavailable");
    }
  }
  return { controllerRequest: vi.fn(), ControllerError: MockControllerError };
});

vi.mock("@/lib/auth", () => ({
  getAuth: async () => ({
    api: { getSession: async () => ({ user: { id: "admin-1", role: "admin" } }) },
  }),
}));

vi.mock("@/lib/controller-client", () => ({ controllerRequest, ControllerError }));

import { POST } from "@/app/api/admin/machines/[machineId]/agent-update-command/route";

describe("POST /api/admin/machines/[machineId]/agent-update-command", () => {
  beforeEach(() => {
    controllerRequest.mockReset();
    process.env.BETTER_AUTH_URL = "http://localhost";
  });

  it("keeps command issuance unavailable until the trusted helper implements the full workflow", async () => {
    const response = await POST(
      new Request("http://localhost/api/admin/machines/machine-1/agent-update-command", {
        method: "POST",
        headers: { origin: "http://localhost", "content-type": "application/json" },
        body: JSON.stringify({ releaseVersion: "2.3.4" }),
      }),
    );

    expect(response.status).toBe(503);
    expect(response.headers.get("cache-control")).toBe("no-store");
    expect(await response.json()).toEqual({ error: "Controller unavailable" });
    expect(controllerRequest).not.toHaveBeenCalled();
  });
});
