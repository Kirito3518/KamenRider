import { machineApi } from "@/lib/machine-api";
import { ControllerError } from "@/lib/controller-client";

type TicketResponse = { ticket: string; expiresAtUtc: string };

const noStore = { "Cache-Control": "no-store" } as const;

function fail(error: string, status: number) {
  return Response.json({ error }, { status, headers: noStore });
}

/**
 * Deployment problems (missing/invalid environment) must reach the Admin as actionable Thai
 * text. machineApi otherwise collapses every unexpected failure into generic English strings,
 * which makes misconfiguration indistinguishable from a real Controller failure.
 */
class VncConfigError extends Error {
  constructor(
    readonly status: number,
    message: string,
  ) {
    super(message);
    this.name = "VncConfigError";
  }
}

/** The Controller accepted the request but returned a body the browser must never use. */
class InvalidTicketError extends Error {
  constructor() {
    super("Controller returned an invalid VNC ticket");
    this.name = "InvalidTicketError";
  }
}

const MISSING_CONTROLLER_URL =
  "ยังไม่ได้ตั้งค่า AGENT_CONTROLLER_URL สำหรับ VNC (ต้องเป็น https origin เท่านั้น)";
const INVALID_CONTROLLER_URL =
  "AGENT_CONTROLLER_URL สำหรับ VNC ไม่ถูกต้อง: ต้องเป็น https origin เท่านั้น (ห้ามมี path, query, hash หรือข้อมูลล็อกอิน)";
const INVALID_PUBLIC_WS_URL =
  "VNC_PUBLIC_WS_URL ไม่ถูกต้อง: ต้องเป็น wss URL แบบ absolute ที่ path /admin/vnc และไม่มี query/hash";
const NOT_READY =
  "เครื่องนี้ยังไม่พร้อมสำหรับ VNC: ต้องอนุมัติแล้ว, Agent ออนไลน์ล่าสุดไม่เกิน 20 วินาที และมี VNC target ที่บันทึกไว้";

/**
 * AGENT_CONTROLLER_URL is the Agent-facing Controller URL. The browser reuses its host for the
 * VNC gateway, so it must be a bare https origin: no credentials, path, query or hash. A plain
 * HTTP or hostname-with-path value would silently resolve to an unusable WSS URL.
 */
function controllerOrigin(value: string | undefined) {
  if (!value) return null;
  try {
    const url = new URL(value);
    if (
      url.protocol !== "https:" ||
      url.username ||
      url.password ||
      url.search ||
      url.hash ||
      url.pathname !== "/"
    )
      return null;
    return url;
  } catch {
    return null;
  }
}

/**
 * Optional browser-facing override for deployments where the Admin browser cannot reach the
 * Controller directly (reverse proxy / separate LAN address). It must already be the final
 * absolute WSS endpoint: protocol wss, pathname exactly /admin/vnc, no credentials/query/hash.
 * Without it the URL is derived from AGENT_CONTROLLER_URL exactly as before.
 */
function publicWebSocketUrl(value: string | undefined) {
  if (!value) return null;
  try {
    const url = new URL(value);
    if (
      url.protocol !== "wss:" ||
      url.username ||
      url.password ||
      url.search ||
      url.hash ||
      url.pathname !== "/admin/vnc"
    )
      return null;
    return url;
  } catch {
    return null;
  }
}

/** Resolve the browser-facing WSS endpoint, or throw a Thai configuration error. */
function resolveWebSocketUrl() {
  const override = publicWebSocketUrl(process.env.VNC_PUBLIC_WS_URL);
  if (process.env.VNC_PUBLIC_WS_URL) {
    if (!override) throw new VncConfigError(503, INVALID_PUBLIC_WS_URL);
    return override.toString();
  }

  if (!process.env.AGENT_CONTROLLER_URL?.trim())
    throw new VncConfigError(503, MISSING_CONTROLLER_URL);
  const controllerUrl = controllerOrigin(process.env.AGENT_CONTROLLER_URL);
  if (!controllerUrl) throw new VncConfigError(503, INVALID_CONTROLLER_URL);

  const webSocketUrl = new URL("/admin/vnc", controllerUrl);
  webSocketUrl.protocol = "wss:";
  return webSocketUrl.toString();
}

function validTicket(value: unknown): value is TicketResponse {
  if (!value || typeof value !== "object" || Array.isArray(value)) return false;
  const record = value as Record<string, unknown>;
  return (
    typeof record.ticket === "string" &&
    /^[A-Za-z0-9_-]{43}$/.test(record.ticket) &&
    typeof record.expiresAtUtc === "string" &&
    Number.isFinite(Date.parse(record.expiresAtUtc)) &&
    Date.parse(record.expiresAtUtc) > Date.now()
  );
}

/**
 * Fixed Thai messages only: Controller internals, configuration values and ticket material must
 * never be echoed to the browser.
 */
function mapFailure(error: unknown): Response | null {
  if (error instanceof VncConfigError) return fail(error.message, error.status);
  if (error instanceof InvalidTicketError)
    return fail("Controller ตอบกลับตั๋ว VNC ที่ไม่ถูกต้อง กรุณาลองใหม่", 502);
  if (error instanceof ControllerError) {
    if (error.status === 409) return fail(NOT_READY, 409);
    if (error.status === 403)
      return fail("Controller ปฏิเสธสิทธิ์การออกตั๋ว VNC ของผู้ดูแลระบบ", 403);
    if (error.status === 404) return fail("ไม่พบเครื่องนี้ใน Controller", 404);
    if (error.status === 400)
      return fail("คำขอเปิดจอ VNC ไม่ถูกต้อง (machineId หรือ origin ไม่ผ่านการตรวจสอบ)", 400);
    return fail("Controller ไม่พร้อมให้บริการออกตั๋ว VNC ในขณะนี้", 503);
  }
  if (error instanceof SyntaxError || error instanceof TypeError)
    return fail("คำขอเปิดจอ VNC ไม่ถูกต้อง", 400);
  return null;
}

export function POST(request: Request) {
  return machineApi(
    request,
    true,
    async (_actor, call) => {
      const body: unknown = await request.json();
      if (
        !body ||
        typeof body !== "object" ||
        Array.isArray(body) ||
        typeof (body as Record<string, unknown>).machineId !== "string" ||
        !(body as Record<string, string>).machineId.trim() ||
        (body as Record<string, string>).machineId.length > 128
      )
        throw new TypeError("Invalid machine ID");
      const machineId = (body as Record<string, string>).machineId;

      // Validate the browser-facing target before spending a Controller ticket. A bad
      // deployment must be reported as configuration, not as "Controller unavailable".
      const webSocketUrl = resolveWebSocketUrl();

      const allowedOrigin = new URL(
        process.env.BETTER_AUTH_URL || request.url,
      ).origin;
      const issued = await call<TicketResponse>(
        "/internal/admin-vnc-tickets",
        {
          method: "POST",
          body: JSON.stringify({ machineId, allowedOrigin }),
        },
      );
      if (!validTicket(issued)) throw new InvalidTicketError();

      return {
        ticket: issued.ticket,
        expiresAtUtc: issued.expiresAtUtc,
        webSocketUrl,
      };
    },
    false,
    mapFailure,
  );
}
