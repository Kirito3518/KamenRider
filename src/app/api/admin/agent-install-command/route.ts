import { createHash } from "node:crypto";
import { readFile } from "node:fs/promises";
import path from "node:path";
import { getAuth } from "@/lib/auth";

function allowedUrl(value: string | undefined) {
  if (!value) return null;
  try {
    const url = new URL(value);
    if (url.username || url.password || url.search || url.hash) return null;
    if (url.protocol !== "https:" && !(url.protocol === "http:" && ["localhost", "127.0.0.1", "[::1]"].includes(url.hostname))) return null;
    return url;
  } catch { return null; }
}

function quoted(value: string) { return `'${value.replaceAll("'", "''")}'`; }

export async function GET(request: Request) {
  const session = await (await getAuth()).api.getSession({ headers: request.headers });
  if (!session) return Response.json({ error: "Authentication required" }, { status: 401 });
  if (session.user.role !== "admin") return Response.json({ error: "Forbidden" }, { status: 403 });

  const base = allowedUrl(process.env.AGENT_INSTALL_BASE_URL || process.env.BETTER_AUTH_URL);
  const packageUrl = allowedUrl(process.env.AGENT_PACKAGE_URL);
  const controllerUrl = allowedUrl(process.env.AGENT_CONTROLLER_URL);
  const packageHash = process.env.AGENT_PACKAGE_SHA256?.toUpperCase();
  const root = process.env.AGENT_TRUST_ROOT_THUMBPRINT?.toUpperCase();
  if (process.env.AGENT_INSTALL_ENABLED !== "true" || !base || !packageUrl || !controllerUrl || controllerUrl.protocol !== "https:" || !/^[A-F0-9]{64}$/.test(packageHash || "") || !/^[A-F0-9]{40}$/.test(root || "")) {
    return Response.json({ available: false, reason: "ยังไม่ได้ตั้งค่าแพ็กเกจ Agent และ URL สำหรับเครื่องลูก" }, { headers: { "Cache-Control": "no-store" } });
  }
  const script = await readFile(path.join(process.cwd(), "public", "agent", "Bootstrap-Agent.ps1"));
  const hash = createHash("sha256").update(script).digest("hex").toUpperCase();
  const scriptUrl = new URL("/agent/Bootstrap-Agent.ps1", base).toString();
  const command = `$u=${quoted(scriptUrl)}; $p=Join-Path $env:TEMP ('kr-agent-'+[guid]::NewGuid().ToString('N')+'.ps1'); try { Invoke-WebRequest -Uri $u -OutFile $p -ErrorAction Stop; if((Get-FileHash -LiteralPath $p -Algorithm SHA256).Hash -ne ${quoted(hash)}) { throw 'Installer script hash mismatch' }; & $p -ControllerUrl ${quoted(controllerUrl.toString())} -PackageUrl ${quoted(packageUrl.toString())} -PackageSha256 ${quoted(packageHash!)} -TrustedRootThumbprint ${quoted(root!)} } finally { Remove-Item -LiteralPath $p -Force -ErrorAction SilentlyContinue }`;
  return Response.json({ available: true, command, scriptUrl }, { headers: { "Cache-Control": "no-store" } });
}
