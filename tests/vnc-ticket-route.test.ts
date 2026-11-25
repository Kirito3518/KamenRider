import { readFileSync } from "node:fs";
import { resolve } from "node:path";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";

type MockCall = (path: string, init?: RequestInit) => Promise<unknown>;
type MockAction = (
  actor: { id: string; role: string },
  call: MockCall,
) => Promise<unknown>;
type MockErrorResponse = (error: unknown) => Response | null;

const h = vi.hoisted(() => {
  class MockControllerError extends Error {
    constructor(public status: number) {
      super("Controller unavailable");
    }
  }
  return { controllerCall: vi.fn(), ControllerError: MockControllerError };
});

// The route imports ControllerError from the Controller client; the real module is server-only,
// so mirror the other route tests and provide the class through a mock.
vi.mock("@/lib/controller-client", () => ({
  controllerRequest: vi.fn(),
  ControllerError: h.ControllerError,
}));

// Faithful mini machineApi: runs the action, then gives the route's errorResponse hook the first
// chance to map failures to Thai responses (exactly what the real machineApi does).
vi.mock("@/lib/machine-api", () => ({
  machineApi: async (
    _request: Request,
    _admin: boolean,
    action: MockAction,
    _publicAccess?: boolean,
    errorResponse?: MockErrorResponse,
  ) => {
    try {
      const result = await action(
        { id: "admin-1", role: "admin" },
        h.controllerCall as MockCall,
      );
      return Response.json(result, {
        headers: { "Cache-Control": "no-store" },
      });
    } catch (error) {
      const mapped = errorResponse?.(error);
      if (mapped) return mapped;
      return Response.json({ error: "Unmapped failure" }, { status: 500 });
    }
  },
}));

import { POST } from "@/app/api/admin/vnc-ticket/route";

const TICKET = "T".repeat(43);
const CONTROLLER_URL = "https://controller.example:7443";
const NOT_READY =
  "เครื่องนี้ยังไม่พร้อมสำหรับ VNC: ต้องอนุมัติแล้ว, Agent ออนไลน์ล่าสุดไม่เกิน 20 วินาที และมี VNC target ที่บันทึกไว้";

function request(body: unknown) {
  return new Request("http://localhost/api/admin/vnc-ticket", {
    method: "POST",
    headers: { origin: "http://localhost", "content-type": "application/json" },
    body: JSON.stringify(body),
  });
}

function issued(overrides: Record<string, unknown> = {}) {
  return {
    ticket: TICKET,
    expiresAtUtc: new Date(Date.now() + 60_000).toISOString(),
    ...overrides,
  };
}

async function errorOf(response: Response) {
  const body = (await response.json()) as { error?: string };
  return body.error;
}

describe("POST /api/admin/vnc-ticket", () => {
  beforeEach(() => {
    h.controllerCall.mockReset();
    process.env.BETTER_AUTH_URL = "http://localhost";
    delete process.env.AGENT_CONTROLLER_URL;
    delete process.env.VNC_PUBLIC_WS_URL;
  });

  afterEach(() => {
    delete process.env.AGENT_CONTROLLER_URL;
    delete process.env.VNC_PUBLIC_WS_URL;
  });

  it("reports a missing AGENT_CONTROLLER_URL as an actionable Thai 503 without calling the Controller", async () => {
    const response = await POST(request({ machineId: "pc-1" }));

    expect(response.status).toBe(503);
    expect(response.headers.get("cache-control")).toBe("no-store");
    expect(await errorOf(response)).toBe(
      "ยังไม่ได้ตั้งค่า AGENT_CONTROLLER_URL สำหรับ VNC (ต้องเป็น https origin เท่านั้น)",
    );
    expect(h.controllerCall).not.toHaveBeenCalled();
  });

  it("rejects a non-https or non-origin AGENT_CONTROLLER_URL with a configuration error", async () => {
    for (const value of [
      "http://controller.example:7443",
      "https://controller.example:7443/controller",
      "https://controller.example:7443?token=secret",
      "https://controller.example:7443#fragment",
      "https://user:secret@controller.example:7443",
      "not-a-url",
    ]) {
      process.env.AGENT_CONTROLLER_URL = value;
      const response = await POST(request({ machineId: "pc-1" }));
      expect(response.status).toBe(503);
      expect(response.headers.get("cache-control")).toBe("no-store");
      const message = await errorOf(response);
      expect(message).toContain("AGENT_CONTROLLER_URL");
      expect(message).not.toContain("secret");
    }
    expect(h.controllerCall).not.toHaveBeenCalled();
  });

  it("derives the browser WSS URL from AGENT_CONTROLLER_URL and forwards the ticket request", async () => {
    process.env.AGENT_CONTROLLER_URL = CONTROLLER_URL;
    const ticket = issued();
    h.controllerCall.mockResolvedValueOnce(ticket);

    const response = await POST(request({ machineId: "pc-1" }));

    expect(response.status).toBe(200);
    expect(response.headers.get("cache-control")).toBe("no-store");
    expect(await response.json()).toEqual({
      ticket: TICKET,
      expiresAtUtc: ticket.expiresAtUtc,
      webSocketUrl: "wss://controller.example:7443/admin/vnc",
    });
    expect(h.controllerCall).toHaveBeenCalledTimes(1);
    const [path, init] = h.controllerCall.mock.calls[0] as [string, RequestInit];
    expect(path).toBe("/internal/admin-vnc-tickets");
    expect(init.method).toBe("POST");
    expect(JSON.parse(String(init.body))).toEqual({
      machineId: "pc-1",
      allowedOrigin: "http://localhost",
    });
  });

  it("uses VNC_PUBLIC_WS_URL for browser reachability and does not require AGENT_CONTROLLER_URL", async () => {
    process.env.VNC_PUBLIC_WS_URL = "wss://vnc.example.com:8443/admin/vnc";
    h.controllerCall.mockResolvedValueOnce(issued());

    const response = await POST(request({ machineId: "pc-1" }));

    expect(response.status).toBe(200);
    expect(response.headers.get("cache-control")).toBe("no-store");
    expect((await response.json()).webSocketUrl).toBe(
      "wss://vnc.example.com:8443/admin/vnc",
    );
    expect(h.controllerCall).toHaveBeenCalledTimes(1);
  });

  it("rejects an unsafe VNC_PUBLIC_WS_URL instead of silently falling back", async () => {
    process.env.AGENT_CONTROLLER_URL = CONTROLLER_URL;
    for (const value of [
      "https://vnc.example.com/admin/vnc",
      "wss://vnc.example.com/other",
      "wss://vnc.example.com/admin/vnc?token=secret",
      "wss://vnc.example.com/admin/vnc#fragment",
      "wss://user:secret@vnc.example.com/admin/vnc",
      "/admin/vnc",
    ]) {
      process.env.VNC_PUBLIC_WS_URL = value;
      const response = await POST(request({ machineId: "pc-1" }));
      expect(response.status).toBe(503);
      expect(response.headers.get("cache-control")).toBe("no-store");
      const message = await errorOf(response);
      expect(message).toContain("VNC_PUBLIC_WS_URL");
      expect(message).not.toContain("secret");
    }
    expect(h.controllerCall).not.toHaveBeenCalled();
  });

  it("maps an invalid or expired Controller ticket to a Thai error", async () => {
    process.env.AGENT_CONTROLLER_URL = CONTROLLER_URL;

    h.controllerCall.mockResolvedValueOnce(issued({ ticket: "too-short" }));
    const invalid = await POST(request({ machineId: "pc-1" }));
    expect(invalid.status).toBe(502);
    expect(invalid.headers.get("cache-control")).toBe("no-store");
    expect(await errorOf(invalid)).toContain("ตั๋ว VNC");

    h.controllerCall.mockResolvedValueOnce(
      issued({ expiresAtUtc: new Date(Date.now() - 1_000).toISOString() }),
    );
    const expired = await POST(request({ machineId: "pc-1" }));
    expect(expired.status).toBe(502);
    expect(await errorOf(expired)).toContain("ตั๋ว VNC");
  });

  it("maps Controller 409 (not approved, stale heartbeat or missing vnc target) to actionable Thai", async () => {
    process.env.AGENT_CONTROLLER_URL = CONTROLLER_URL;
    h.controllerCall.mockRejectedValueOnce(new h.ControllerError(409));

    const response = await POST(request({ machineId: "pc-1" }));

    expect(response.status).toBe(409);
    expect(response.headers.get("cache-control")).toBe("no-store");
    expect(await errorOf(response)).toBe(NOT_READY);
  });

  it("maps Controller 403/404/400 to specific Thai errors without leaking Controller internals", async () => {
    process.env.AGENT_CONTROLLER_URL = CONTROLLER_URL;
    const cases: Array<[number, number]> = [
      [403, 403],
      [404, 404],
      [400, 400],
    ];
    for (const [controllerStatus, expectedStatus] of cases) {
      h.controllerCall.mockRejectedValueOnce(new h.ControllerError(controllerStatus));
      const response = await POST(request({ machineId: "pc-1" }));
      expect(response.status).toBe(expectedStatus);
      expect(response.headers.get("cache-control")).toBe("no-store");
      const raw = await response.text();
      expect(raw).not.toContain("Controller unavailable");
      expect(raw).not.toContain("Invalid request");
      expect(raw).not.toContain("Operation rejected");
      expect(JSON.parse(raw).error).toMatch(/[\u0E00-\u0E7F]/);
    }
  });

  it("maps an unexpected Controller outage to a Thai 503", async () => {
    process.env.AGENT_CONTROLLER_URL = CONTROLLER_URL;
    h.controllerCall.mockRejectedValueOnce(new h.ControllerError(503));

    const response = await POST(request({ machineId: "pc-1" }));

    expect(response.status).toBe(503);
    expect(await errorOf(response)).toContain("Controller");
  });

  it("rejects malformed bodies and invalid machine IDs without calling the Controller", async () => {
    process.env.AGENT_CONTROLLER_URL = CONTROLLER_URL;
    for (const body of [{}, { machineId: " " }, { machineId: 42 }, { machineId: "x".repeat(129) }]) {
      const response = await POST(request(body));
      expect(response.status).toBe(400);
      expect(response.headers.get("cache-control")).toBe("no-store");
      expect(await errorOf(response)).toContain("VNC");
    }

    const malformed = await POST(
      new Request("http://localhost/api/admin/vnc-ticket", {
        method: "POST",
        headers: { origin: "http://localhost", "content-type": "application/json" },
        body: "{not-json",
      }),
    );
    expect(malformed.status).toBe(400);
    expect(h.controllerCall).not.toHaveBeenCalled();
  });
});

/**
 * viewer.html is a static asset, so its security contract can only be guarded by inspecting the
 * shipped file text. This keeps the password fallback from regressing back to a silent-only
 * failure and re-checks the restrictions the Admin UI depends on.
 */
describe("public/novnc/viewer.html contract", () => {
  const viewer = readFileSync(
    resolve(process.cwd(), "public", "novnc", "viewer.html"),
    "utf8",
  );

  it("tries the managed password first but asks for the machine password after securityfailure", () => {
    expect(viewer).toContain('const managedVncPassword = "KamenVNC"');
    expect(viewer).toContain("credentialsrequired");
    expect(viewer).toContain("sendCredentials({ password })");
    expect(viewer).toContain("securityfailure");
    expect(viewer).toContain("onAuthenticationFailure");
    expect(viewer).toContain('id="password-prompt"');
    expect(viewer).toContain('type="password"');
  });

  it("requests fresh ticket material before retrying a rejected password", () => {
    expect(viewer).toContain("kr-vnc-need-ticket");
    expect(viewer).toContain("kr-vnc-reconnect");
    expect(viewer).toContain("kr-vnc-ticket-error");
    expect(viewer).toContain("ticketWaitMs");
  });

  it("distinguishes authentication, unreachable/TLS and gateway failures in status text", () => {
    expect(viewer).toContain("VNC ปฏิเสธการยืนยันตัวตน");
    expect(viewer).toContain("ใบรับรอง TLS");
    expect(viewer).toContain("Gateway VNC");
    expect(viewer).toContain("การเชื่อมต่อหลุด");
  });

  it("keeps same-origin postMessage validation, ticket regex and wss /admin/vnc checks", () => {
    expect(viewer).toContain(
      "event.origin !== window.location.origin || event.source !== window.parent",
    );
    expect(viewer).toContain("/^[A-Za-z0-9_-]{43}$/");
    expect(viewer).toContain('socketUrl.protocol !== "wss:"');
    expect(viewer).toContain('socketUrl.pathname !== "/admin/vnc"');
    expect(viewer).toContain("socketUrl.search || socketUrl.hash");
    expect(viewer).toContain("session.viewOnly = true");
    expect(viewer).toContain("extendedClipboardPseudoEncoding");
  });

  it("never logs or persists the password", () => {
    expect(viewer).not.toContain("console.log");
    expect(viewer).not.toContain("console.info");
    expect(viewer).not.toContain("localStorage");
    expect(viewer).not.toContain("sessionStorage");
  });
});
