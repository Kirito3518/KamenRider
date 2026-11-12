import assert from "node:assert/strict";
import { test } from "node:test";
import { createHash } from "node:crypto";
import { createServer } from "node:http";
import { existsSync, readFileSync, unlinkSync, writeFileSync } from "node:fs";
import Database from "better-sqlite3";
const base = process.env.TEST_AUTH_URL || "http://localhost:3002";
const headers = {
  "content-type": "application/json",
  origin: base,
  "X-Authenticated-Actor-Id": "forged",
  "X-Authenticated-Actor-Role": "admin",
  "X-Service-Credential": "forged",
};
let agentUpdateAdminCookie;
let agentUpdateRegularCookie;
let agentUpdateAdminId;
const call = (path, body, cookie, method) =>
  fetch(`${base}/api${path}`, {
    method: method || (body ? "POST" : "GET"),
    headers: { ...headers, ...(cookie ? { cookie } : {}) },
    body: body ? JSON.stringify(body) : undefined,
  });
const makeControllerMachine = (overrides) => ({
  machineId: "pc",
  approvalStatus: "approved",
  displayName: "PC",
  localIp: "192.168.1.4",
  vncLocalIp: "192.168.1.5",
  hourlyPriceMinorUnits: 100,
  moonlightPort: 47989,
  sunshinePort: 47990,
  configRevision: 3,
  appliedConfigRevision: 3,
  readiness: "ready",
  canRent: true,
  lastSeenAt: "2026-11-08T00:00:00Z",
  agentVersion: "1.0.0",
  activeAdminVncSessions: 0,
  ...overrides,
});
test("machine API session boundaries, validation, redaction and controller failures", async (t) => {
  assert.equal((await call("/my-leases")).status, 401);
  assert.equal((await call("/my-leases/rental-1/extend", { additionalSeconds: 60, expectedRevision: 1, idempotencyKey: "unauthenticated" })).status, 401);
  assert.equal((await call("/admin/machines/pc/approve", {})).status, 401);
  assert.equal((await call("/admin/vnc-ticket", { machineId: "pc" })).status, 401);
  assert.equal((await call("/admin/vnc-target-check", { machineId: "pc", targetAddress: "10.0.0.8" })).status, 401);
  assert.equal((await call("/machines")).status, 503);
  const seen = [];
  let leaseOwnerId = "";
  const server = createServer(async (req, res) => {
    let raw = "";
    for await (const c of req) raw += c;
    seen.push({
      url: req.url,
      headers: req.headers,
      body: raw ? JSON.parse(raw) : null,
    });
    res.setHeader("content-type", "application/json");
    if (req.headers["x-service-credential"] !== "test-only") {
      res.writeHead(401).end("{}");
      return;
    }
    if (req.url === "/internal/agent-enrollment-tickets") {
      assert.equal(req.method, "POST");
      res.end(JSON.stringify({ ticket: "A".repeat(43), expiresAtUtc: new Date(Date.now() + 86_400_000).toISOString() }));
      return;
    }
    if (req.url === "/internal/admin-vnc-tickets") {
      assert.equal(req.method, "POST");
      res.end(JSON.stringify({ ticket: "V".repeat(43), expiresAtUtc: new Date(Date.now() + 60_000).toISOString() }));
      return;
    }
    if (req.url === "/internal/admin-vnc-target-probes") {
      assert.equal(req.method, "POST");
      res.end(JSON.stringify({ supported: true }));
      return;
    }
    if (req.url === "/internal/machines") {
      res.end(JSON.stringify([
        makeControllerMachine({
          macAddress: "001122334455",
          publicKeyFingerprint: "secret-management-fingerprint",
          readinessDiagnostics: [
            { checkId: "baseline", status: "passed" },
            { checkId: "bootChallenge", status: "unavailable" },
          ],
        }),
        makeControllerMachine({ machineId: "held", displayName: "held", canRent: false }),
        makeControllerMachine({ machineId: "not-ready", displayName: "not-ready", readiness: "notReady", canRent: false }),
        makeControllerMachine({ machineId: "offline", displayName: "offline", readiness: "unreachable", canRent: false }),
        makeControllerMachine({ machineId: "pending", displayName: "pending", approvalStatus: "pending", canRent: true }),
        makeControllerMachine({ machineId: "truthy-can-rent", displayName: "truthy-can-rent", canRent: 1 }),
        makeControllerMachine({ machineId: "missing-can-rent", displayName: "missing-can-rent", canRent: undefined }),
      ]));
      return;
    }
    const extendMatch = req.url.match(/^\/internal\/leases\/([^/]+)\/extend$/);
    if (extendMatch) {
      if (extendMatch[1] !== "rental-1" || req.headers["x-authenticated-actor-id"] !== leaseOwnerId) {
        res.writeHead(403).end("{}");
        return;
      }
      res.end(JSON.stringify({ id: "rental-1", machineId: "pc", state: "active", revision: 1, pendingRevision: 2, expiresAtUtc: "2026-11-06T12:00:00Z" }));
      return;
    }
    if (req.url.includes("/pair")) {
      res.writeHead(403).end("{}");
      return;
    }
    if (req.url.includes("expectedRevision=999")) {
      res.writeHead(409).end("{}");
      return;
    }
    res.end("{}");
  });
  await new Promise((resolve) => server.listen(5089, "127.0.0.1", resolve));
  try {
    const suffix = crypto.randomUUID().replaceAll("-", "").slice(0, 10);
    const signup = await call("/auth/sign-up/email", {
      name: "Machine test",
      username: `machine_${suffix}`,
      email: `${suffix}@example.com`,
      password: "Test-password-928!",
    });
    assert.equal(signup.status, 200);
    const created = await signup.json();
    leaseOwnerId = created.user.id;
    let cookie = signup.headers
      .getSetCookie()
      .map((c) => c.split(";")[0])
      .join("; ");
    assert.equal(
      (await call("/admin/machines/pc/approve", {}, cookie)).status,
      403,
    );
    assert.equal((await call("/admin/vnc-ticket", { machineId: "pc" }, cookie)).status, 403);
    assert.equal((await call("/admin/vnc-target-check", { machineId: "pc", targetAddress: "10.0.0.8" }, cookie)).status, 403);
    const inventory = await call("/machines");
    assert.equal(inventory.status, 200);
    const publicData = await inventory.text();
    const publicMachines = JSON.parse(publicData);
    await t.test("public machine inventory includes only explicitly approved rentable machines", () => {
      assert.deepEqual(publicMachines.map((machine) => machine.id), ["pc"]);
      assert.equal(publicMachines[0].status, "available");
    });
    assert.ok(publicMachines.every((machine) => !("readinessDiagnostics" in machine)));
    assert.ok(!publicData.includes("readinessDiagnostics"));
    for (const secret of [
      "192.168.1.4",
      "192.168.1.5",
      "001122334455",
      "47990",
      "secret-management-fingerprint",
    ])
      assert.ok(!publicData.includes(secret));
    assert.equal(
      (
        await call(
          "/my-leases/not-owner/pair",
          { pairingId: "id", pin: "1234", idempotencyKey: "request" },
          cookie,
        )
      ).status,
      403,
    );
    const ownerRequest = seen.at(-1);
    assert.equal(
      ownerRequest.headers["x-authenticated-actor-id"],
      created.user.id,
    );
    assert.equal(ownerRequest.headers["x-authenticated-actor-role"], "user");
    assert.equal(ownerRequest.body.userId, created.user.id);
    const extension = { additionalSeconds: 3600, expectedRevision: 1, idempotencyKey: "extend-once" };
    assert.equal((await call("/my-leases/rental-1/extend", { ...extension, additionalSeconds: 0 }, cookie)).status, 400);
    assert.equal((await call("/my-leases/not-owner/extend", extension, cookie)).status, 403);
    const extendedResponse = await call("/my-leases/rental-1/extend", extension, cookie);
    assert.equal(extendedResponse.status, 200);
    assert.equal((await extendedResponse.json()).pendingRevision, 2);
    const extensionRequest = seen.at(-1);
    assert.equal(extensionRequest.url, "/internal/leases/rental-1/extend");
    assert.equal(extensionRequest.headers["x-authenticated-actor-id"], created.user.id);
    assert.equal(extensionRequest.headers["x-authenticated-actor-role"], "user");
    assert.deepEqual(extensionRequest.body, extension);
    const db = new Database(process.env.TEST_AUTH_DB);
    try {
      db.prepare("UPDATE user SET role='admin' WHERE id=?").run(
        created.user.id,
      );
    } finally {
      db.close();
    }
    const login = await call("/auth/sign-in/email", {
      email: `${suffix}@example.com`,
      password: "Test-password-928!",
    });
    cookie = login.headers
      .getSetCookie()
      .map((c) => c.split(";")[0])
      .join("; ");
    const patch = {
      displayName: "PC",
      hourlyPriceMinorUnits: 100,
      moonlightPort: 47989,
      sunshinePort: 47990,
      vncLocalIp: "192.168.1.4",
      expectedRevision: 999,
    };
    await t.test("omitting vncLocalIp preserves the saved Admin-confirmed target", async () => {
      const withoutVncTarget = {
        displayName: "PC",
        hourlyPriceMinorUnits: 100,
        moonlightPort: 47989,
        sunshinePort: 47990,
        expectedRevision: 1,
      };
      assert.equal((await call("/admin/machines/pc", withoutVncTarget, cookie, "PATCH")).status, 200);
      assert.equal(seen.at(-1).body.vncLocalIp, "192.168.1.5");
    });
    const clearVncTarget = { ...patch, vncLocalIp: null, expectedRevision: 1 };
    assert.equal((await call("/admin/machines/pc", clearVncTarget, cookie, "PATCH")).status, 200);
    assert.equal(seen.at(-1).body.vncLocalIp, null);
    const validVncTarget = { ...patch, expectedRevision: 1 };
    assert.equal((await call("/admin/machines/pc", validVncTarget, cookie, "PATCH")).status, 200);
    assert.equal(seen.at(-1).body.vncLocalIp, "192.168.1.4");
    for (const invalid of [
      { ...patch, hourlyPriceMinorUnits: -1 },
      { ...patch, moonlightPort: 65536 },
    ])
      assert.equal(
        (await call("/admin/machines/pc", invalid, cookie, "PATCH")).status,
        400,
      );
    assert.equal(
      (await call("/admin/machines/pc", patch, cookie, "PATCH")).status,
      409,
    );
    assert.equal(
      seen.at(-1).headers["x-authenticated-actor-id"],
      created.user.id,
    );
    assert.equal(seen.at(-1).headers["x-authenticated-actor-role"], "admin");
    const adminMachines = await call("/admin/machines", undefined, cookie);
    assert.equal(adminMachines.status, 200);
    assert.equal((await adminMachines.json())[0].vncLocalIp, "192.168.1.5");
    assert.equal((await call("/admin/vnc-ticket", { machineId: " " }, cookie)).status, 400);
    const vncTicket = await call("/admin/vnc-ticket", { machineId: "pc" }, cookie);
    assert.equal(vncTicket.status, 200);
    assert.equal(vncTicket.headers.get("cache-control"), "no-store");
    const vncResult = await vncTicket.json();
    assert.equal(vncResult.ticket, "V".repeat(43));
    assert.equal(vncResult.webSocketUrl, "wss://localhost:7443/admin/vnc");
    assert.ok(Number.isFinite(Date.parse(vncResult.expiresAtUtc)));
    const vncRequest = seen.at(-1);
    assert.equal(vncRequest.url, "/internal/admin-vnc-tickets");
    assert.equal(vncRequest.headers["x-authenticated-actor-id"], created.user.id);
    assert.equal(vncRequest.headers["x-authenticated-actor-role"], "admin");
    assert.deepEqual(vncRequest.body, { machineId: "pc", allowedOrigin: base });
    assert.equal(
      (await call("/admin/vnc-target-check", { machineId: "pc" }, cookie)).status,
      400,
    );
    const targetCheck = await call(
      "/admin/vnc-target-check",
      {
        machineId: "pc",
        targetAddress: "10.0.0.18",
        host: "vnc.example.com",
        port: 22,
      },
      cookie,
    );
    assert.equal(targetCheck.status, 200);
    assert.equal(targetCheck.headers.get("cache-control"), "no-store");
    assert.deepEqual(await targetCheck.json(), { supported: true });
    const targetProbeRequest = seen.at(-1);
    assert.equal(targetProbeRequest.url, "/internal/admin-vnc-target-probes");
    assert.equal(targetProbeRequest.headers["x-authenticated-actor-id"], created.user.id);
    assert.equal(targetProbeRequest.headers["x-authenticated-actor-role"], "admin");
    assert.deepEqual(targetProbeRequest.body, {
      machineId: "pc",
      targetAddress: "10.0.0.18",
    });
    assert.equal(
      (
        await fetch(`${base}/api/admin/machines/pc/wake`, {
          method: "POST",
          headers: { ...headers, origin: "https://evil.example", cookie },
          body: "{}",
        })
      ).status,
      403,
    );
  } finally {
    await new Promise((resolve) => server.close(resolve));
  }
  assert.equal((await call("/machines")).status, 503);
});

test("agent install command is admin-only and withheld until configuration validates", async () => {
  const suffix = crypto.randomUUID().replaceAll("-", "").slice(0, 10);
  const signup = await call("/auth/sign-up/email", {
    name: "Agent install test",
    username: `agent_${suffix}`,
    email: `${suffix}@example.com`,
    password: "Test-password-928!",
  });
  assert.equal(signup.status, 200);
  const created = await signup.json();
  const db = new Database(process.env.TEST_AUTH_DB);
  try {
    db.prepare("UPDATE user SET role='admin' WHERE id=?").run(created.user.id);
  } finally {
    db.close();
  }
  const login = await call("/auth/sign-in/email", {
    email: `${suffix}@example.com`,
    password: "Test-password-928!",
  });
  const cookie = login.headers
    .getSetCookie()
    .map((value) => value.split(";")[0])
    .join("; ");
  agentUpdateAdminCookie = cookie;
  agentUpdateAdminId = created.user.id;

  const regularSuffix = crypto.randomUUID().replaceAll("-", "").slice(0, 10);
  const regularSignup = await call("/auth/sign-up/email", {
    name: "Agent install regular user",
    username: `agent_regular_${regularSuffix}`,
    email: `${regularSuffix}@example.com`,
    password: "Test-password-928!",
  });
  assert.equal(regularSignup.status, 200);
  const regularUserCookie = regularSignup.headers
    .getSetCookie()
    .map((value) => value.split(";")[0])
    .join("; ");
  agentUpdateRegularCookie = regularUserCookie;

  const ticketRequests = [];
  const ticketExpiresAtUtc = new Date(Date.now() + 86_400_000).toISOString();
  const ticketServer = createServer(async (req, res) => {
    let bodyBytes = 0;
    for await (const chunk of req) bodyBytes += chunk.length;
    ticketRequests.push({ method: req.method, url: req.url, credential: req.headers["x-service-credential"], bodyBytes });
    res.setHeader("content-type", "application/json");
    if (req.method !== "POST" || req.url !== "/internal/agent-enrollment-tickets" || req.headers["x-service-credential"] !== "test-only") {
      res.writeHead(401).end("{}");
      return;
    }
    res.end(JSON.stringify({ ticket: "A".repeat(43), expiresAtUtc: ticketExpiresAtUtc }));
  });
  await new Promise((resolve) => ticketServer.listen(5089, "127.0.0.1", resolve));
  try {
    const getResponse = await call("/admin/agent-install-command", undefined, cookie, "GET");
    assert.equal(getResponse.status, 405);
    assert.deepEqual(ticketRequests, []);

    const unauthenticated = await call("/admin/agent-install-command", undefined, undefined, "POST");
    assert.equal(unauthenticated.status, 401);
    assert.equal((await call("/admin/agent-install-command", undefined, regularUserCookie, "POST")).status, 403);

    const wrongOrigin = await fetch(`${base}/api/admin/agent-install-command`, {
      method: "POST",
      headers: { ...headers, origin: "https://evil.example", cookie },
    });
    assert.equal(wrongOrigin.status, 403);
    assert.deepEqual(ticketRequests, []);

    const response = await call("/admin/agent-install-command", undefined, cookie, "POST");
    assert.equal(response.status, 200);
    assert.equal(response.headers.get("cache-control"), "no-store");
    const result = await response.json();
    const expected = process.env.TEST_AGENT_INSTALL_EXPECTED || "available";
    if (expected === "unavailable") {
      assert.equal(result.available, false);
      assert.equal("command" in result, false);
      assert.equal("scriptUrl" in result, false);
      return;
    }

    assert.equal(result.available, true);
    assert.equal(result.expiresAtUtc, ticketExpiresAtUtc);
    assert.deepEqual(ticketRequests, [{ method: "POST", url: "/internal/agent-enrollment-tickets", credential: "test-only", bodyBytes: 0 }]);
    const baseUrl = process.env.AGENT_INSTALL_BASE_URL || process.env.BETTER_AUTH_URL;
    const packageUrl = new URL(process.env.AGENT_PACKAGE_URL);
    const controllerUrl = new URL(process.env.AGENT_CONTROLLER_URL);
    const scriptUrl = new URL("/agent/Bootstrap-Agent.ps1", baseUrl).toString();
    const returnUrl = new URL("/admin/machines", baseUrl).toString();
    const scriptHash = createHash("sha256")
    .update(readFileSync(new URL("../public/agent/Bootstrap-Agent.ps1", import.meta.url)))
    .digest("hex")
    .toUpperCase();
    const bootstrapDownload = await fetch(scriptUrl);
    assert.equal(bootstrapDownload.status, 200);
    const servedScriptHash = createHash("sha256")
    .update(Buffer.from(await bootstrapDownload.arrayBuffer()))
    .digest("hex")
    .toUpperCase();
    assert.equal(servedScriptHash, scriptHash);
    const quoted = (value) => `'${value.replaceAll("'", "''")}'`;
    assert.equal(result.scriptUrl, scriptUrl);
    assert.ok(result.command.includes(`-UseBasicParsing`));
    assert.ok(result.command.includes(`[ScriptBlock]::Create`));
    assert.ok(result.command.includes(`RawContentStream`));
    assert.ok(!result.command.includes(`$env:TEMP`));
    assert.ok(result.command.includes(`-ControllerUrl ${quoted(controllerUrl.toString())}`));
    assert.ok(result.command.includes(`-VncAllowedSourceAddress ${quoted(process.env.TEST_AGENT_INSTALL_VNC_SOURCE_EXPECTED)}`));
    assert.ok(result.command.includes(`-PackageUrl ${quoted(packageUrl.toString())}`));
    assert.ok(result.command.includes(`-InstallReturnUrl ${quoted(returnUrl)}`));
    assert.ok(result.command.includes(`-PackageSha256 ${quoted(process.env.AGENT_PACKAGE_SHA256.toUpperCase())}`));
    assert.ok(result.command.includes(`-TrustedRootThumbprint ${quoted(process.env.AGENT_TRUST_ROOT_THUMBPRINT.toUpperCase())}`));
    assert.ok(/-EnrollmentTicket '[A-Za-z0-9_-]{43}'/.test(result.command), "command contains an enrollment ticket");
    assert.ok(result.command.includes(quoted(scriptHash)));
  } finally {
    await new Promise((resolve) => ticketServer.close(resolve));
  }
});

test("agent update release and command routes enforce Admin origin and forward explicit selections", async () => {
  const unauthenticatedList = await call("/admin/agent-releases");
  assert.equal(unauthenticatedList.status, 401);
  assert.equal(unauthenticatedList.headers.get("cache-control"), "no-store");
  const unauthenticatedPublish = await call("/admin/agent-releases", undefined, undefined, "POST");
  assert.equal(unauthenticatedPublish.status, 401);
  assert.equal(unauthenticatedPublish.headers.get("cache-control"), "no-store");
  const unauthenticatedCommand = await call("/admin/machines/pc/agent-update-command", undefined, undefined, "POST");
  assert.equal(unauthenticatedCommand.status, 401);
  assert.equal(unauthenticatedCommand.headers.get("cache-control"), "no-store");

  assert.ok(agentUpdateAdminCookie && agentUpdateRegularCookie && agentUpdateAdminId, "the preceding auth test supplies Admin and regular sessions");
  const adminCookie = agentUpdateAdminCookie;
  const regularCookie = agentUpdateRegularCookie;
  const adminId = agentUpdateAdminId;

  const requests = [];
  const ticket = "U".repeat(43);
  const recoveryTicket = "R".repeat(43);
  const expiresAtUtc = new Date(Date.now() + 10 * 60_000).toISOString();
  const release = {
    version: "2.0.0",
    channel: "stable",
    releaseNotes: "test release",
    packageSha256: "B".repeat(64),
    packageSizeBytes: 1024,
    sourceCommit: "0123456789ab",
    sourceDirty: false,
    publishedAtUtc: new Date().toISOString(),
  };
  const controller = createServer(async (req, res) => {
    const chunks = [];
    for await (const chunk of req) chunks.push(chunk);
    const rawBody = Buffer.concat(chunks).toString("utf8");
    requests.push({
      method: req.method,
      url: req.url,
      headers: req.headers,
      rawBody,
      body: rawBody && req.headers["content-type"]?.startsWith("application/json") ? JSON.parse(rawBody) : null,
    });
    res.setHeader("content-type", "application/json");
    if (req.headers["x-service-credential"] !== "test-only") {
      res.writeHead(401).end("{}");
      return;
    }
    if (req.method === "GET" && req.url === "/internal/agent-releases") {
      res.end(JSON.stringify([release]));
      return;
    }
    if (req.method === "POST" && req.url === "/internal/agent-releases") {
      res.end(JSON.stringify(release));
      return;
    }
    if (req.method === "POST" && req.url === "/internal/machines/pc/agent-updates") {
      if (process.env.TEST_AGENT_UPDATE_EXPECTED === "generic-conflict") {
        res.writeHead(409).end("{}");
        return;
      }
      if (process.env.TEST_AGENT_UPDATE_EXPECTED === "retained-release-unavailable") {
        res.writeHead(409).end(JSON.stringify({
          code: "release_unavailable",
          available: false,
          reason: "Stored package does not match its published SHA-256.",
        }));
        return;
      }
      res.end(JSON.stringify({
        operationId: "operation-1",
        machineId: "pc",
        ticket,
        expiresAtUtc,
        releaseVersion: "2.0.0",
        targetVersion: "2.0.0",
        targetPackageSha256: release.packageSha256,
      }));
      return;
    }
    if (req.method === "POST" && req.url === "/internal/machines/pc/agent-updates/operation-1/recovery-ticket") {
      res.end(JSON.stringify({
        operationId: "operation-1",
        machineId: "pc",
        ticket: recoveryTicket,
        expiresAtUtc,
        releaseVersion: "2.0.0",
        targetVersion: "2.0.0",
        targetPackageSha256: release.packageSha256,
      }));
      return;
    }
    res.writeHead(404).end("{}");
  });
  await new Promise((resolve) => controller.listen(5089, "127.0.0.1", resolve));

  const scriptPath = new URL("../public/agent/Update-Agent.ps1", import.meta.url);
  const scriptWasPresent = existsSync(scriptPath);
  const originalScript = scriptWasPresent ? readFileSync(scriptPath) : null;
  if (!scriptWasPresent) writeFileSync(scriptPath, "# test-only update script\n");
  try {
    assert.equal((await call("/admin/machines/pc/agent-update-command", undefined, adminCookie, "GET")).status, 405);
    const regularCommand = await call("/admin/machines/pc/agent-update-command", { releaseVersion: "2.0.0" }, regularCookie);
    assert.equal(regularCommand.status, 403);
    assert.equal(regularCommand.headers.get("cache-control"), "no-store");
    const crossOrigin = await fetch(`${base}/api/admin/machines/pc/agent-update-command`, {
      method: "POST",
      headers: { ...headers, origin: "https://evil.example", cookie: adminCookie },
      body: JSON.stringify({ releaseVersion: "2.0.0" }),
    });
    assert.equal(crossOrigin.status, 403);
    assert.equal(crossOrigin.headers.get("cache-control"), "no-store");
    assert.deepEqual(requests, []);

    const releases = await call("/admin/agent-releases", undefined, adminCookie);
    assert.equal(releases.status, 200);
    assert.equal(releases.headers.get("cache-control"), "no-store");
    assert.deepEqual(await releases.json(), [release]);
    const listRequest = requests.at(-1);
    assert.equal(listRequest.method, "GET");
    assert.equal(listRequest.url, "/internal/agent-releases");
    assert.equal(listRequest.headers["x-authenticated-actor-id"], adminId);
    assert.equal(listRequest.headers["x-authenticated-actor-role"], "admin");
    assert.equal(listRequest.headers["x-service-credential"], "test-only");

    const form = new FormData();
    form.append("version", "2.0.0");
    form.append("channel", "stable");
    form.append("releaseNotes", "test release");
    form.append("package", new Blob(["package bytes"], { type: "application/zip" }), "AgentPackage.zip");
    const wrongOriginUpload = await fetch(`${base}/api/admin/agent-releases`, {
      method: "POST",
      headers: { origin: "https://evil.example", cookie: adminCookie },
      body: form,
    });
    assert.equal(wrongOriginUpload.status, 403);
    assert.equal(requests.length, 1);
    const published = await fetch(`${base}/api/admin/agent-releases`, {
      method: "POST",
      headers: { origin: base, cookie: adminCookie, "x-authenticated-actor-id": "forged", "x-authenticated-actor-role": "admin" },
      body: form,
    });
    assert.equal(published.status, 200);
    assert.equal(published.headers.get("cache-control"), "no-store");
    const uploadRequest = requests.at(-1);
    assert.equal(uploadRequest.method, "POST");
    assert.equal(uploadRequest.url, "/internal/agent-releases");
    assert.match(uploadRequest.headers["content-type"], /^multipart\/form-data; boundary=/);
    assert.equal(uploadRequest.headers["x-authenticated-actor-id"], adminId);
    assert.equal(uploadRequest.headers["x-authenticated-actor-role"], "admin");
    assert.ok(uploadRequest.rawBody.includes("AgentPackage.zip"));
    assert.ok(uploadRequest.rawBody.includes("package bytes"));

    const beforeCommand = requests.length;
    const command = await call("/admin/machines/pc/agent-update-command", { releaseVersion: "2.0.0" }, adminCookie);
    assert.equal(command.status, 503);
    assert.equal(command.headers.get("cache-control"), "no-store");
    assert.deepEqual(await command.json(), { error: "Controller unavailable" });
    assert.equal(requests.length, beforeCommand, "command issuance remains disabled until the trusted helper workflow is complete");
  } finally {
    if (!scriptWasPresent) unlinkSync(scriptPath);
    else writeFileSync(scriptPath, originalScript);
    await new Promise((resolve) => controller.close(resolve));
  }
});
