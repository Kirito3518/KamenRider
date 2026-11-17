import { createHash } from "node:crypto";
import { readFile } from "node:fs/promises";
import path from "node:path";
import { isLoopbackHostname, resolveVncSourceAddress } from "@/lib/agent-install-vnc-source.mjs";
import { buildOneLinerInstaller } from "@/lib/agent-install-script.mjs";

export const dynamic = "force-dynamic";

function allowedUrl(value: string | undefined, originOnly = false): URL | null {
  if (!value) return null;
  try {
    const url = new URL(value);
    if (url.username || url.password || url.search || url.hash) return null;
    if (url.protocol !== "https:" && !(url.protocol === "http:" && isLoopbackHostname(url.hostname))) return null;
    if (originOnly && url.pathname !== "/") return null;
    return url;
  } catch {
    return null;
  }
}

function textResponse(message: string, status: number): Response {
  return new Response(message + "\n", {
    status,
    headers: { "Content-Type": "text/plain; charset=utf-8", "Cache-Control": "no-store" },
  });
}

export async function GET(request: Request): Promise<Response> {
  if (process.env.AGENT_INSTALL_ENABLED !== "true") {
    return textResponse("Agent installation is not enabled on this deployment.", 503);
  }

  const ticket = new URL(request.url).searchParams.get("k") ?? "";
  if (!/^[A-Za-z0-9_-]{43}$/.test(ticket)) {
    return textResponse("Invalid or missing installer key.", 400);
  }

  const base = allowedUrl(process.env.AGENT_INSTALL_BASE_URL, true);
  const packageUrl = allowedUrl(process.env.AGENT_PACKAGE_URL);
  const controllerUrl = allowedUrl(process.env.AGENT_CONTROLLER_URL, true);
  const packageHash = process.env.AGENT_PACKAGE_SHA256?.toUpperCase();
  const trustThumbprint = process.env.AGENT_TRUST_ROOT_THUMBPRINT?.toUpperCase();
  const vncAllowedSourceAddress = controllerUrl
    ? resolveVncSourceAddress(controllerUrl.hostname, process.env.AGENT_VNC_ALLOWED_SOURCE_IP)
    : null;
  if (
    !base ||
    !packageUrl ||
    !controllerUrl ||
    controllerUrl.protocol !== "https:" ||
    !/^[A-F0-9]{64}$/.test(packageHash || "") ||
    !/^[A-F0-9]{40}$/.test(trustThumbprint || "") ||
    !vncAllowedSourceAddress
  ) {
    return textResponse("Agent installer is not configured on this deployment.", 503);
  }

  let bootstrap;
  try {
    bootstrap = await readFile(path.join(process.cwd(), "public", "agent", "Bootstrap-Agent.ps1"));
  } catch {
    return textResponse("Installer payload is missing on the server.", 503);
  }
  const bootstrapSha256 = createHash("sha256").update(bootstrap).digest("hex").toUpperCase();

  const installUrl = new URL(request.url).toString();
  const bootstrapUrl = new URL("/agent/Bootstrap-Agent.ps1", base).toString();
  const controllerCertUrl = new URL("/agent/controller-root.cer", base).toString();
  const installReturnUrl = new URL("/admin/machines", base).toString();

  const script = buildOneLinerInstaller({
    installUrl,
    bootstrapUrl,
    bootstrapSha256,
    controllerUrl: controllerUrl.toString(),
    packageUrl: packageUrl.toString(),
    packageSha256: packageHash!,
    trustThumbprint: trustThumbprint!,
    enrollmentTicket: ticket,
    installReturnUrl,
    vncAllowedSourceAddress,
    controllerCertUrl,
  });

  return new Response(script, {
    headers: { "Content-Type": "text/plain; charset=utf-8", "Cache-Control": "no-store" },
  });
}
