import { createHash } from "node:crypto";
import { readFile } from "node:fs/promises";
import path from "node:path";
import { getAuth } from "@/lib/auth";
import { controllerRequest } from "@/lib/controller-client";

function isLoopback(hostname: string) {
  const host = hostname.replace(/^\[(.*)\]$/, "$1").toLowerCase();
  if (host === "localhost" || host === "::1") return true;
  const octets = host.split(".").map(Number);
  return octets.length === 4 && octets[0] === 127 && octets.every((part) => Number.isInteger(part) && part >= 0 && part <= 255);
}

function allowedUrl(value: string | undefined, originOnly = false) {
  if (!value) return null;
  try {
    const url = new URL(value);
    if (url.username || url.password || url.search || url.hash) return null;
    if (url.protocol !== "https:" && !(url.protocol === "http:" && isLoopback(url.hostname))) return null;
    if (originOnly && url.pathname !== "/") return null;
    return url;
  } catch { return null; }
}

function quoted(value: string) { return `'${value.replaceAll("'", "''")}'`; }

export async function GET(request: Request) {
  const session = await (await getAuth()).api.getSession({ headers: request.headers });
  if (!session) return Response.json({ error: "Authentication required" }, { status: 401, headers: { "Cache-Control": "no-store" } });
  if (session.user.role !== "admin") return Response.json({ error: "Forbidden" }, { status: 403, headers: { "Cache-Control": "no-store" } });

  const base = allowedUrl(process.env.AGENT_INSTALL_BASE_URL, true);
  const packageUrl = allowedUrl(process.env.AGENT_PACKAGE_URL);
  const controllerUrl = allowedUrl(process.env.AGENT_CONTROLLER_URL, true);
  const packageHash = process.env.AGENT_PACKAGE_SHA256?.toUpperCase();
  const root = process.env.AGENT_TRUST_ROOT_THUMBPRINT?.toUpperCase();
  if (process.env.AGENT_INSTALL_ENABLED !== "true" || !base || !packageUrl || !controllerUrl || controllerUrl.protocol !== "https:" || !/^[A-F0-9]{64}$/.test(packageHash || "") || !/^[A-F0-9]{40}$/.test(root || "")) {
    return Response.json({ available: false, reason: "ยังไม่ได้ตั้งค่าแพ็กเกจ Agent และ URL สำหรับเครื่องลูก" }, { headers: { "Cache-Control": "no-store" } });
  }
  let script: Buffer;
  try {
    script = await readFile(path.join(process.cwd(), "public", "agent", "Bootstrap-Agent.ps1"));
  } catch {
    return Response.json({ available: false, reason: "ไฟล์ติดตั้ง Agent ยังไม่พร้อม" }, { headers: { "Cache-Control": "no-store" } });
  }
  const hash = createHash("sha256").update(script).digest("hex").toUpperCase();
  const scriptUrl = new URL("/agent/Bootstrap-Agent.ps1", base).toString();
  const returnUrl = new URL("/admin/machines", base).toString();
  let ticket: string;
  try {
    const issued = await controllerRequest<{ ticket: string; expiresAtUtc: string }>("/internal/agent-enrollment-tickets", { method: "POST" });
    if (!/^[A-Za-z0-9_-]{43}$/.test(issued.ticket) || !Number.isFinite(Date.parse(issued.expiresAtUtc)) || Date.parse(issued.expiresAtUtc) <= Date.now()) {
      throw new Error("Invalid enrollment ticket response");
    }
    ticket = issued.ticket;
  } catch {
    return Response.json({ available: false, reason: "ออก ticket ลงทะเบียนจาก Controller ไม่สำเร็จ" }, { headers: { "Cache-Control": "no-store" } });
  }
  const command = `$u=${quoted(scriptUrl)}; $r=Invoke-WebRequest -UseBasicParsing -Uri $u -ErrorAction Stop; $m=New-Object System.IO.MemoryStream; try { $r.RawContentStream.CopyTo($m); $b=$m.ToArray() } finally { $m.Dispose(); $r.RawContentStream.Dispose() }; $h=[BitConverter]::ToString([Security.Cryptography.SHA256]::Create().ComputeHash($b)).Replace('-',''); if($h -ne ${quoted(hash)}) { throw 'Installer script hash mismatch' }; $s=[Text.Encoding]::UTF8.GetString($b); & ([ScriptBlock]::Create($s)) -ControllerUrl ${quoted(controllerUrl.toString())} -PackageUrl ${quoted(packageUrl.toString())} -PackageSha256 ${quoted(packageHash!)} -TrustedRootThumbprint ${quoted(root!)} -EnrollmentTicket ${quoted(ticket)} -InstallReturnUrl ${quoted(returnUrl)}`;
  return Response.json({ available: true, command, scriptUrl }, { headers: { "Cache-Control": "no-store" } });
}
