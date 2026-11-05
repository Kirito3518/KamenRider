import { spawn } from "node:child_process";
import { randomBytes } from "node:crypto";
import { mkdtempSync, rmSync } from "node:fs";
import { tmpdir } from "node:os";
import { join } from "node:path";

const port = 3002;
const scenarios = [
  { name: "configured", expected: "available", vncSource: "127.0.0.1", full: true, overrides: {} },
  { name: "configured-vnc-source", expected: "available", vncSource: "192.168.1.10", overrides: { AGENT_VNC_ALLOWED_SOURCE_IP: "192.168.1.10" } },
  { name: "private-controller-ip-fallback", expected: "available", vncSource: "192.168.50.4", overrides: { AGENT_CONTROLLER_URL: "https://192.168.50.4:7443" } },
  { name: "public-controller-without-vnc-source", expected: "unavailable", overrides: { AGENT_CONTROLLER_URL: "https://controller.example:7443", AGENT_VNC_ALLOWED_SOURCE_IP: "" } },
  { name: "public-vnc-source-rejected", expected: "unavailable", overrides: { AGENT_VNC_ALLOWED_SOURCE_IP: "203.0.113.10" } },
  { name: "cidr-vnc-source-rejected", expected: "unavailable", overrides: { AGENT_VNC_ALLOWED_SOURCE_IP: "192.168.1.0/24" } },
  { name: "ipv6-vnc-source-rejected", expected: "unavailable", overrides: { AGENT_VNC_ALLOWED_SOURCE_IP: "::1" } },
  { name: "controller-ticket-unavailable", expected: "unavailable", overrides: { CONTROLLER_INTERNAL_URL: "http://127.0.0.1:5090" } },
  { name: "disabled", expected: "unavailable", overrides: { AGENT_INSTALL_ENABLED: "false" } },
  { name: "missing-install-base-url", expected: "unavailable", overrides: { AGENT_INSTALL_BASE_URL: "" } },
  { name: "localhost-subdomain-base", expected: "unavailable", overrides: { AGENT_INSTALL_BASE_URL: "http://shop.localhost:3002" } },
  { name: "non-loopback-http-base", expected: "unavailable", overrides: { AGENT_INSTALL_BASE_URL: "http://192.0.2.20:3002" } },
  { name: "package-credentials", expected: "unavailable", overrides: { AGENT_PACKAGE_URL: "https://user:secret@packages.example/AgentPackage.zip" } },
  { name: "package-query", expected: "unavailable", overrides: { AGENT_PACKAGE_URL: "https://packages.example/AgentPackage.zip?token=secret" } },
  { name: "package-fragment", expected: "unavailable", overrides: { AGENT_PACKAGE_URL: "https://packages.example/AgentPackage.zip#download" } },
  { name: "missing-package-url", expected: "unavailable", overrides: { AGENT_PACKAGE_URL: "" } },
  { name: "controller-path", expected: "unavailable", overrides: { AGENT_CONTROLLER_URL: "https://localhost:7443/controller" } },
  { name: "controller-query", expected: "unavailable", overrides: { AGENT_CONTROLLER_URL: "https://localhost:7443/?token=secret" } },
  { name: "bad-package-hash", expected: "unavailable", overrides: { AGENT_PACKAGE_SHA256: "not-a-hash" } },
  { name: "bad-trust-thumbprint", expected: "unavailable", overrides: { AGENT_TRUST_ROOT_THUMBPRINT: "1234" } },
];

let exitCode = 0;
for (const scenario of scenarios) {
  const directory = mkdtempSync(join(tmpdir(), "kamen-auth-test-"));
  const base = `http://localhost:${port}`;
  const database = join(directory, "auth.sqlite");
  const env = {
    ...process.env,
    CONTROLLER_INTERNAL_URL: "http://127.0.0.1:5089",
    CONTROLLER_SERVICE_CREDENTIAL: "test-only",
    BETTER_AUTH_URL: base,
    BETTER_AUTH_SECRET: randomBytes(48).toString("base64url"),
    AUTH_DATABASE_PATH: database,
    TEST_AUTH_DB: database,
    TEST_AUTH_URL: base,
    TEST_AGENT_INSTALL_EXPECTED: scenario.expected,
    TEST_AGENT_INSTALL_VNC_SOURCE_EXPECTED: scenario.vncSource || "",
    AGENT_INSTALL_ENABLED: "true",
    AGENT_INSTALL_BASE_URL: base,
    AGENT_PACKAGE_URL: "https://packages.example/publisher's/AgentPackage.zip",
    AGENT_PACKAGE_SHA256: "A".repeat(64),
    AGENT_CONTROLLER_URL: "https://localhost:7443",
    AGENT_VNC_ALLOWED_SOURCE_IP: "",
    AGENT_TRUST_ROOT_THUMBPRINT: "B".repeat(40),
    ...scenario.overrides,
  };
  const server = spawn(
    process.execPath,
    [
      "node_modules/next/dist/bin/next",
      "start",
      "--hostname",
      "127.0.0.1",
      "--port",
      String(port),
    ],
    { env, stdio: ["ignore", "pipe", "pipe"] },
  );
  let startupOutput = "";
  let stopped = false;
  const serverExit = new Promise((resolve) =>
    server.once("exit", () => {
      stopped = true;
      resolve();
    }),
  );
  server.stdout.on("data", (chunk) => {
    startupOutput += chunk.toString();
    process.stdout.write(chunk);
  });
  server.stderr.on("data", (chunk) => process.stderr.write(chunk));

  try {
    let ready = false;
    for (let attempt = 0; attempt < 100 && !stopped; attempt++) {
      try {
        if (startupOutput.includes("Ready in") && (await fetch(`${base}/api/auth/get-session`)).ok) {
          ready = true;
          break;
        }
      } catch {}
      await new Promise((resolve) => setTimeout(resolve, 200));
    }
    if (!ready) throw new Error(`Test server did not become ready for ${scenario.name}`);

    const testArgs = scenario.full
      ? ["--test", "tests/machines.test.mjs"]
      : ["--test", "--test-name-pattern=agent install command", "tests/machines.test.mjs"];
    const tests = spawn(process.execPath, testArgs, { env, stdio: "inherit" });
    const scenarioExit = await new Promise((resolve) =>
      tests.once("exit", (code) => resolve(code ?? 1)),
    );
    if (scenarioExit !== 0) {
      exitCode = scenarioExit;
      break;
    }
  } finally {
    if (!stopped) server.kill();
    await serverExit;
    console.log(`Test-only database (${scenario.name}): ${database}`);
    rmSync(directory, { recursive: true, force: true });
  }
}
process.exitCode = exitCode;
