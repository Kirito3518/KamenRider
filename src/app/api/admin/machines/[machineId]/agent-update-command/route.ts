import { randomUUID } from "node:crypto";
import { machineApi, segment, text } from "@/lib/machine-api";
import { ControllerError, type SafeRecoveryHead } from "@/lib/controller-client";
import {
  AgentMaintenanceAssetsUnavailableError,
  verifyDeploymentAgentMaintenanceAssets,
  type AgentMaintenanceRuntime,
  type VerifiedAgentMaintenanceAssets,
} from "@/lib/agent-maintenance-assets";

type ControllerRelease = {
  version: string;
  packageSha256: string;
  targetRuntime?: string;
  agentUpdateChallengeProtocol?: number;
};

type ControllerTicket = {
  operationId: string;
  machineId: string;
  ticket: string;
  expiresAtUtc: string;
  releaseVersion: string;
  targetVersion: string;
  targetPackageSha256: string;
  chainId?: string;
  currentHeadOperationId?: string;
  generation?: number;
  keyFingerprint?: string;
  targetPackageLength?: number;
};

function allowedOrigin(value: string | undefined) {
  if (!value) return null;
  try {
    const url = new URL(value);
    if (url.username || url.password || url.search || url.hash || url.pathname !== "/") return null;
    if (url.protocol !== "https:" && !(url.protocol === "http:" && ["127.0.0.1", "localhost", "[::1]"].includes(url.hostname)))
      return null;
    return url;
  } catch {
    return null;
  }
}

function quoted(value: string) {
  return `'${value.replaceAll("'", "''")}'`;
}

function unavailable(reason: string) {
  return { available: false as const, reason };
}

function conflictResponse(error: unknown): Response | null {
  if (error instanceof ControllerError && error.status === 409 && error.safeRecoveryHead) {
    return Response.json(error.safeRecoveryHead, {
      status: 409,
      headers: { "Cache-Control": "no-store" },
    });
  }
  return null;
}

export async function POST(
  request: Request,
  { params }: { params: Promise<{ machineId: string }> },
) {
  const { machineId } = await params;
  return machineApi(
    request,
    true,
    async (actor, call) => {
      if (!/^[0-9a-fA-F]{32}$/.test(machineId)) throw new TypeError("Invalid machine id");
      if (!actor.id) throw new ControllerError(401);

      const body: unknown = await request.json();
      if (typeof body !== "object" || body === null || Array.isArray(body)) throw new TypeError("Invalid request");
      const record = body as Record<string, unknown>;
      const keys = Object.keys(record);
      if (keys.some((key) => key !== "releaseVersion" && key !== "requestId"))
        throw new TypeError("Unexpected request field");
      if (record.requestId !== undefined) text(record.requestId);
      const releaseVersion = text(record.releaseVersion);
      const requestId = typeof record.requestId === "string" ? record.requestId : randomUUID();

      if (process.env.AGENT_UPDATE_ENABLED !== "true")
        return unavailable("ยังไม่ได้เปิดใช้คำสั่งอัปเดต Agent บนเครื่องนี้");
      const controllerUrl = allowedOrigin(process.env.AGENT_CONTROLLER_URL);
      const assetBaseUrl = allowedOrigin(process.env.AGENT_INSTALL_BASE_URL);
      const trustedRootThumbprint = process.env.AGENT_TRUST_ROOT_THUMBPRINT?.toUpperCase();
      if (!controllerUrl || controllerUrl.protocol !== "https:" || !assetBaseUrl ||
          !/^[A-F0-9]{40}$/.test(trustedRootThumbprint || ""))
        return unavailable("ยังไม่ได้ตั้งค่า Controller URL, asset base URL หรือ trusted root สำหรับคำสั่งอัปเดต Agent");

      const releases = await call<ControllerRelease[]>("/internal/agent-releases");
      const release = releases.find((candidate) => candidate.version === releaseVersion);
      if (!release) return unavailable("ไม่พบรีลีสที่ระบุใน Controller");
      const runtime = release.targetRuntime;
      if (runtime !== "win-x64" && runtime !== "win-arm64")
        return unavailable("รีลีสนี้ไม่ระบุ runtime ที่รองรับสำหรับการอัปเดตจากระยะไกล");
      if (release.agentUpdateChallengeProtocol !== 2)
        return unavailable("รีลีสนี้ไม่รองรับโปรโตคอลอัปเดตแบบ protocol 2");

      let assets: VerifiedAgentMaintenanceAssets;
      try {
        assets = await verifyDeploymentAgentMaintenanceAssets(runtime as AgentMaintenanceRuntime);
      } catch (error) {
        if (error instanceof AgentMaintenanceAssetsUnavailableError)
          return unavailable("ยังไม่มี maintenance helper/wrapper ที่ผ่านการ pin ใน deployment นี้");
        throw error;
      }

      const ticket = await call<ControllerTicket>(
        `/internal/machines/${segment(machineId)}/agent-updates`,
        { method: "POST", body: JSON.stringify({ releaseVersion, requestId }) },
      );
      if (!/^[0-9a-fA-F]{32}$/.test(ticket.operationId) ||
          !/^[A-Za-z0-9_-]{43}$/.test(ticket.ticket) ||
          !Number.isFinite(Date.parse(ticket.expiresAtUtc)) ||
          Date.parse(ticket.expiresAtUtc) <= Date.now() ||
          ticket.releaseVersion !== releaseVersion ||
          !/^[0-9a-fA-F]{64}$/.test(ticket.targetPackageSha256) ||
          !ticket.chainId || !ticket.currentHeadOperationId ||
          typeof ticket.targetPackageLength !== "number" || !Number.isSafeInteger(ticket.targetPackageLength) || ticket.targetPackageLength < 1 ||
          ticket.targetPackageLength > 104_857_600 ||
          !/^[0-9a-fA-F]{64}$/.test(ticket.keyFingerprint || "") ||
          typeof ticket.generation !== "number" || !Number.isSafeInteger(ticket.generation) || ticket.generation < 1)
        throw new Error("Invalid Agent update reservation response");

      const wrapperRelativeUrl = assets.wrapper.relativeUrl;
      const helperRelativeUrl = assets.helper.relativeUrl;
      const wrapperUrl = new URL(wrapperRelativeUrl, assetBaseUrl).toString();
      const adminReturnUrl = new URL("/admin/agent-releases", assetBaseUrl).toString();
      const command = [
        `$u=${quoted(wrapperUrl)};`,
        "$r=Invoke-WebRequest -UseBasicParsing -Uri $u -ErrorAction Stop;",
        "$m=New-Object System.IO.MemoryStream;",
        "try { $r.RawContentStream.CopyTo($m); $b=$m.ToArray() } finally { $m.Dispose(); $r.RawContentStream.Dispose() };",
        `if($b.Length -ne ${assets.wrapper.length}) { throw 'Maintenance wrapper length mismatch' };`,
        "$h=[BitConverter]::ToString([Security.Cryptography.SHA256]::Create().ComputeHash($b)).Replace('-','');",
        `if($h -ne ${quoted(assets.wrapper.sha256.toUpperCase())}) { throw 'Maintenance wrapper hash mismatch' };`,
        "$s=[Text.Encoding]::UTF8.GetString($b);",
        "& ([ScriptBlock]::Create($s))",
        `-ControllerUrl ${quoted(controllerUrl.toString())}`,
        `-MachineId ${quoted(machineId.toLowerCase())}`,
        `-OperationId ${quoted(ticket.operationId.toLowerCase())}`,
        `-ChainId ${quoted(ticket.chainId.toLowerCase())}`,
        `-HeadOperationId ${quoted(ticket.currentHeadOperationId.toLowerCase())}`,
        `-Generation ${ticket.generation}`,
        `-EnrollmentKeyFingerprint ${quoted(ticket.keyFingerprint!.toUpperCase())}`,
        `-PackageLength ${ticket.targetPackageLength}`,
        `-UpdateTicket ${quoted(ticket.ticket)}`,
        `-ReleaseVersion ${quoted(ticket.releaseVersion)}`,
        `-TargetVersion ${quoted(ticket.targetVersion)}`,
        `-PackageSha256 ${quoted(ticket.targetPackageSha256.toUpperCase())}`,
        `-TrustedRootThumbprint ${quoted(trustedRootThumbprint!)}`,
        `-AdminReturnUrl ${quoted(adminReturnUrl)}`,
        `-AssetBaseUrl ${quoted(assetBaseUrl.toString())}`,
        `-TargetRuntime ${quoted(runtime)}`,
        `-WrapperSha256 ${quoted(assets.wrapper.sha256.toUpperCase())}`,
        `-WrapperLength ${assets.wrapper.length}`,
        `-HelperRelativeUrl ${quoted(helperRelativeUrl)}`,
        `-HelperSha256 ${quoted(assets.helper.sha256.toUpperCase())}`,
        `-HelperLength ${assets.helper.length}`,
        `-HelperProtocol ${assets.helperProtocol}`,
      ].join(" ");

      return {
        available: true as const,
        command,
        machineId: machineId.toLowerCase(),
        operationId: ticket.operationId.toLowerCase(),
        chainId: ticket.chainId,
        currentHeadOperationId: ticket.currentHeadOperationId,
        generation: ticket.generation,
        keyFingerprint: ticket.keyFingerprint!.toUpperCase(),
        targetPackageLength: ticket.targetPackageLength,
        updateTicket: ticket.ticket,
        releaseVersion: ticket.releaseVersion,
        targetVersion: ticket.targetVersion,
        targetPackageSha256: ticket.targetPackageSha256.toUpperCase(),
        targetRuntime: runtime,
        expiresAtUtc: ticket.expiresAtUtc,
        wrapperRelativeUrl,
        wrapperSha256: assets.wrapper.sha256.toUpperCase(),
        wrapperLength: assets.wrapper.length,
        helperRelativeUrl,
        helperSha256: assets.helper.sha256.toUpperCase(),
        helperLength: assets.helper.length,
        helperProtocol: assets.helperProtocol,
      };
    },
    false,
    conflictResponse,
  );
}

export type { SafeRecoveryHead };
