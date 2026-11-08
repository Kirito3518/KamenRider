import { readFile } from "node:fs/promises";
import path from "node:path";
import { createHash } from "node:crypto";
import { machineApi, segment } from "@/lib/machine-api";

type UpdateTicketResponse = {
  operationId: string;
  machineId: string;
  ticket: string;
  expiresAtUtc: string;
  releaseVersion: string;
  targetVersion: string;
  targetPackageSha256: string;
};

function loopback(host: string) {
  return ["localhost", "127.0.0.1", "[::1]", "::1"].includes(host.toLowerCase());
}

function configuredUrl(value: string | undefined, originOnly = false) {
  if (!value) return null;
  try {
    const url = new URL(value);
    if (url.username || url.password || url.search || url.hash) return null;
    if (url.protocol !== "https:" && !(url.protocol === "http:" && loopback(url.hostname))) return null;
    if (originOnly && url.pathname !== "/") return null;
    return url;
  } catch {
    return null;
  }
}

function quoted(value: string) {
  return `'${value.replaceAll("'", "''")}'`;
}

function isObject(value: unknown): value is Record<string, unknown> {
  return typeof value === "object" && value !== null && !Array.isArray(value);
}

function validateIssuedTicket(issued: UpdateTicketResponse, machineId: string, requestedRelease: string | null, requestedOperation: string | null) {
  if (!issued || issued.machineId !== machineId || !/^[A-Za-z0-9_-]{43}$/.test(issued.ticket)
    || typeof issued.operationId !== "string" || !issued.operationId || issued.operationId.length > 128
    || !Number.isFinite(Date.parse(issued.expiresAtUtc)) || Date.parse(issued.expiresAtUtc) <= Date.now()
    || typeof issued.releaseVersion !== "string" || !issued.releaseVersion
    || typeof issued.targetVersion !== "string" || !issued.targetVersion
    || !/^[A-Fa-f0-9]{64}$/.test(issued.targetPackageSha256)
    || (requestedRelease !== null && issued.releaseVersion !== requestedRelease)
    || (requestedOperation !== null && issued.operationId !== requestedOperation))
    throw new Error("Controller returned an invalid update ticket");
}

export async function POST(request: Request, { params }: { params: Promise<{ machineId: string }> }) {
  const { machineId: routeMachineId } = await params;
  return machineApi(request, true, async (_actor, call) => {
    const machineId = routeMachineId.trim();
    if (!machineId || machineId.length > 128) throw new TypeError("Invalid machine ID");
    const body: unknown = await request.json();
    if (!isObject(body)) throw new TypeError("Invalid update request");

    const requestedOperation = body.operationId;
    const recovery = typeof requestedOperation === "string";
    const operationId = recovery ? requestedOperation.trim() : null;
    if (recovery && (!operationId || operationId.length > 128)) throw new TypeError("Invalid operation ID");
    const releaseVersion = recovery ? null : typeof body.releaseVersion === "string" ? body.releaseVersion.trim() : "";
    if (!recovery && (!releaseVersion || releaseVersion.length > 128)) throw new TypeError("Invalid release version");

    const base = configuredUrl(process.env.AGENT_INSTALL_BASE_URL || process.env.BETTER_AUTH_URL, true);
    const controller = configuredUrl(process.env.AGENT_CONTROLLER_URL, true);
    const thumbprint = process.env.AGENT_TRUST_ROOT_THUMBPRINT?.toUpperCase();
    if (process.env.AGENT_INSTALL_ENABLED !== "true" || !base || !controller || !/^[A-F0-9]{40}$/.test(thumbprint || ""))
      return { available: false, reason: "Agent update URLs or trusted root are not configured" };
    if (controller.protocol !== "https:" && !(controller.protocol === "http:" && loopback(controller.hostname)))
      return { available: false, reason: "Controller URL must use HTTPS or a loopback development URL" };

    let script: Buffer;
    try {
      script = await readFile(path.join(process.cwd(), "public", "agent", "Update-Agent.ps1"));
    } catch {
      return { available: false, reason: "Agent update script is not ready" };
    }

    const internalPath = `/internal/machines/${segment(machineId)}/agent-updates`;
    const issued = recovery
      ? await call<UpdateTicketResponse>(`${internalPath}/${segment(operationId!)}/recovery-ticket`, { method: "POST" })
      : await call<UpdateTicketResponse>(internalPath, { method: "POST", body: JSON.stringify({ releaseVersion }) });
    validateIssuedTicket(issued, machineId, releaseVersion, operationId);

    const scriptHash = createHash("sha256").update(script).digest("hex").toUpperCase();
    const scriptUrl = new URL("/agent/Update-Agent.ps1", base).toString();
    const returnUrl = new URL("/admin/agent-releases", base).toString();
    const command = `$u=${quoted(scriptUrl)}; $r=Invoke-WebRequest -UseBasicParsing -Uri $u -ErrorAction Stop; $m=New-Object System.IO.MemoryStream; try { $r.RawContentStream.CopyTo($m); $b=$m.ToArray() } finally { $m.Dispose(); $r.RawContentStream.Dispose() }; $h=[BitConverter]::ToString([Security.Cryptography.SHA256]::Create().ComputeHash($b)).Replace('-',''); if($h -ne ${quoted(scriptHash)}) { throw 'Agent update script hash mismatch' }; $s=[Text.Encoding]::UTF8.GetString($b); & ([ScriptBlock]::Create($s)) -ControllerUrl ${quoted(controller.toString())} -MachineId ${quoted(machineId)} -OperationId ${quoted(issued.operationId)} -UpdateTicket ${quoted(issued.ticket)} -ReleaseVersion ${quoted(issued.releaseVersion)} -TargetVersion ${quoted(issued.targetVersion)} -PackageSha256 ${quoted(issued.targetPackageSha256.toUpperCase())} -TrustedRootThumbprint ${quoted(thumbprint!)} -AdminReturnUrl ${quoted(returnUrl)}`;

    return {
      available: true,
      command,
      scriptUrl,
      machineId,
      operationId: issued.operationId,
      ticket: issued.ticket,
      expiresAtUtc: issued.expiresAtUtc,
      releaseVersion: issued.releaseVersion,
      targetVersion: issued.targetVersion,
      targetPackageSha256: issued.targetPackageSha256,
    };
  });
}
