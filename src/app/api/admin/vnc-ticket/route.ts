import { machineApi } from "@/lib/machine-api";

type TicketResponse = {
  ticket: string;
  expiresAtUtc: string;
};

export function POST(request: Request) {
  return machineApi(request, true, async (_actor, call) => {
    const body = await request.json();
    if (
      !body ||
      typeof body !== "object" ||
      Array.isArray(body) ||
      typeof body.machineId !== "string" ||
      !body.machineId.trim() ||
      body.machineId.length > 128
    ) {
      throw new TypeError("Invalid machine ID");
    }

    let controllerUrl: URL;
    try {
      controllerUrl = new URL(process.env.AGENT_CONTROLLER_URL || "");
    } catch {
      throw new Error("Controller public URL is not configured");
    }
    if (
      controllerUrl.protocol !== "https:" ||
      controllerUrl.username ||
      controllerUrl.password ||
      controllerUrl.search ||
      controllerUrl.hash ||
      controllerUrl.pathname !== "/"
    ) {
      throw new Error("Controller public URL is invalid");
    }

    const allowedOrigin = new URL(
      process.env.BETTER_AUTH_URL || request.url,
    ).origin;
    const issued = await call<TicketResponse>(
      "/internal/admin-vnc-tickets",
      {
        method: "POST",
        body: JSON.stringify({ machineId: body.machineId, allowedOrigin }),
      },
    );
    if (
      !issued ||
      typeof issued.ticket !== "string" ||
      !/^[A-Za-z0-9_-]{43}$/.test(issued.ticket) ||
      typeof issued.expiresAtUtc !== "string" ||
      !Number.isFinite(Date.parse(issued.expiresAtUtc)) ||
      Date.parse(issued.expiresAtUtc) <= Date.now()
    ) {
      throw new Error("Controller returned an invalid VNC ticket");
    }

    const webSocketUrl = new URL("/admin/vnc", controllerUrl);
    webSocketUrl.protocol = "wss:";
    return {
      ticket: issued.ticket,
      expiresAtUtc: issued.expiresAtUtc,
      webSocketUrl: webSocketUrl.toString(),
    };
  });
}
