import assert from "node:assert/strict";
import { test } from "node:test";
import { createServer } from "node:http";
import Database from "better-sqlite3";
const base = process.env.TEST_AUTH_URL || "http://localhost:3002";
const headers = {
  "content-type": "application/json",
  origin: base,
  "X-Authenticated-Actor-Id": "forged",
  "X-Authenticated-Actor-Role": "admin",
  "X-Service-Credential": "forged",
};
const call = (path, body, cookie, method) =>
  fetch(`${base}/api${path}`, {
    method: method || (body ? "POST" : "GET"),
    headers: { ...headers, ...(cookie ? { cookie } : {}) },
    body: body ? JSON.stringify(body) : undefined,
  });
test("machine API session boundaries, validation, redaction and controller failures", async () => {
  assert.equal((await call("/my-leases")).status, 401);
  assert.equal((await call("/admin/machines/pc/approve", {})).status, 401);
  assert.equal((await call("/machines")).status, 503);
  const seen = [];
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
    if (req.url === "/internal/machines") {
      res.end(
        JSON.stringify([
          {
            machineId: "pc",
            approvalStatus: "approved",
            displayName: "PC",
            hourlyPriceMinorUnits: 100,
            readiness: "unknown",
            localIp: "192.168.1.4",
            macAddress: "001122334455",
            sunshinePort: 47990,
            publicKeyFingerprint: "secret-management-fingerprint",
          },
        ]),
      );
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
    let cookie = signup.headers
      .getSetCookie()
      .map((c) => c.split(";")[0])
      .join("; ");
    assert.equal(
      (await call("/admin/machines/pc/approve", {}, cookie)).status,
      403,
    );
    const inventory = await call("/machines");
    assert.equal(inventory.status, 200);
    const publicData = await inventory.text();
    for (const secret of [
      "192.168.1.4",
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
      expectedRevision: 999,
    };
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
