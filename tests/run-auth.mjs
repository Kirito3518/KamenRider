import { spawn } from "node:child_process";
import { randomBytes } from "node:crypto";
import { mkdtempSync } from "node:fs";
import { tmpdir } from "node:os";
import { join } from "node:path";

const directory = mkdtempSync(join(tmpdir(), "kamen-auth-test-"));
const base = "http://localhost:3001";
const database = join(directory, "auth.sqlite");
const env = { ...process.env, BETTER_AUTH_URL: base, BETTER_AUTH_SECRET: randomBytes(48).toString("base64url"), AUTH_DATABASE_PATH: database, TEST_AUTH_DB: database, TEST_AUTH_URL: base };
const server = spawn(process.execPath, ["node_modules/next/dist/bin/next", "start", "--hostname", "127.0.0.1", "--port", "3001"], { env, stdio: ["ignore", "pipe", "pipe"] });
let startupOutput = "";
server.stdout.on("data", chunk => { startupOutput += chunk.toString(); process.stdout.write(chunk); });
server.stderr.on("data", chunk => process.stderr.write(chunk));
let stopped = false;
server.on("exit", () => { stopped = true; });
try {
  let ready = false;
  for (let attempt = 0; attempt < 100 && !stopped; attempt++) {
    try { if (startupOutput.includes("Ready in") && (await fetch(`${base}/api/auth/get-session`)).ok) { ready = true; break; } } catch {}
    await new Promise(resolve => setTimeout(resolve, 200));
  }
  if (!ready) throw new Error("Test server did not become ready");
  const tests = spawn(process.execPath, ["--test", "tests/auth.test.mjs", "tests/avatars.test.mjs", "tests/roles.test.mjs", "tests/admin-members.test.mjs"], { env, stdio: "inherit" });
  process.exitCode = await new Promise(resolve => tests.on("exit", code => resolve(code ?? 1)));
} finally {
  server.kill();
  console.log(`Test-only database: ${database}`);
}
