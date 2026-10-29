import assert from "node:assert/strict";
import { test } from "node:test";

const base = process.env.TEST_AUTH_URL || "http://localhost:3001";
test("older sessions explain how to reauthenticate for unlinking", async () => {
  const { authError } = await import("../src/lib/auth-errors.ts");
  assert.match(authError({ code: "SESSION_NOT_FRESH" }), /ออกจากระบบ.*เข้าสู่ระบบ/);
});

test("balances format satang as baht without losing cents", async () => {
  const { formatBalance } = await import("../src/lib/money.ts");
  assert.equal(formatBalance(0), "฿0.00");
  assert.equal(formatBalance(10), "฿0.10");
  assert.equal(formatBalance(12345), "฿123.45");
});

test("real credential/session lifecycle and account boundaries", async () => {
  const suffix = crypto.randomUUID().replaceAll("-", "").slice(0, 12);
  const user = { name: "Auth test", username: `test_${suffix}`, email: `${suffix}@example.com`, password: "Test-password-928!", balanceSatang: 999999, role: "admin" };
  let cookie = "";
  async function request(path, body, authenticated = true, origin = base, retries = 0) {
    const res = await fetch(`${base}/api/auth${path}`, {
      method: body ? "POST" : "GET",
      headers: { "Content-Type": "application/json", Origin: origin, ...(authenticated && cookie ? { Cookie: cookie } : {}) },
      ...(body ? { body: JSON.stringify(body) } : {}), redirect: "manual",
    });
    if (res.status === 429 && retries < 2) {
      await new Promise(resolve => setTimeout(resolve, (Number(res.headers.get("X-Retry-After")) || 10) * 1000 + 100));
      return request(path, body, authenticated, origin, retries + 1);
    }
    return res;
  }
  assert.equal((await request("/list-accounts", undefined, false)).status, 401);
  assert.equal((await request("/link-social", { provider: "google" }, false)).status, 401);
  assert.equal((await request("/sign-up/email", user, false, "https://untrusted.example")).status, 403);
  const signup = await request("/sign-up/email", user, false);
  assert.equal(signup.status, 200, await signup.clone().text());
  cookie = signup.headers.getSetCookie().map(c => c.split(";")[0]).join("; ");
  assert.ok(cookie.includes("session_token"));
  const created = await signup.json();
  assert.equal(created.user.username, user.username);
  assert.equal(created.user.role, "user", "signup must not allow privilege escalation");
  assert.equal(created.user.balanceSatang, 0, "signup must not accept a client-supplied balance");
  assert.match(created.user.image, /^\/avatars\/robot-\d{2}\.svg$/);
  const duplicate = await request("/sign-up/email", { ...user, email: `other_${user.email}` }, false);
  assert.ok(duplicate.status >= 400);
  const duplicateEmail = await request("/sign-up/email", { ...user, username: `${user.username}_2` }, false);
  assert.ok(duplicateEmail.status >= 400 || !(await duplicateEmail.json()).token);
  assert.ok((await request("/sign-in/username", { username: user.username, password: "incorrect" }, false)).status >= 400);
  const session = await (await request("/get-session")).json();
  assert.equal(session.user.id, created.user.id);
  assert.equal(session.user.role, "user");
  await request("/update-user", { role: "admin" });
  assert.equal((await (await request("/get-session")).json()).user.role, "user", "profile updates must not grant admin");
  assert.equal(session.user.balanceSatang, 0);
  await request("/update-user", { balanceSatang: 500000 });
  assert.equal((await (await request("/get-session")).json()).user.balanceSatang, 0, "clients must not edit their balance");
  assert.equal(session.user.image, created.user.image);
  const emptyPhoto = await request("/update-user", { image: null });
  assert.equal(emptyPhoto.status, 200);
  assert.equal((await (await request("/get-session")).json()).user.image, created.user.image);
  const accounts = await (await request("/list-accounts")).json();
  assert.equal(accounts.length, 1);
  assert.equal(accounts[0].providerId, "credential");
  assert.equal(accounts[0].password, undefined);
  const unlink = await request("/unlink-account", { accountId: accounts[0].id });
  assert.equal(unlink.status, 400);
  assert.equal((await unlink.json()).code, "FAILED_TO_UNLINK_LAST_ACCOUNT");
  const providerImage = "https://example.com/provider-avatar.png";
  const second = await request("/sign-up/email", { ...user, username: `${user.username}_b`, email: `b_${user.email}`, image: providerImage }, false);
  const secondCookie = second.headers.getSetCookie().map(c => c.split(";")[0]).join("; ");
  assert.equal(second.status, 200);
  assert.equal((await second.json()).user.image, providerImage);
  const ownCookie = cookie;
  cookie = secondCookie;
  const ownAccounts = await (await request("/list-accounts")).json();
  assert.notEqual(ownAccounts[0].userId, created.user.id);
  if (process.env.TEST_AUTH_DB) {
    const { default: Database } = await import("better-sqlite3");
    const db = new Database(process.env.TEST_AUTH_DB);
    db.prepare("UPDATE user SET balanceSatang = ? WHERE id = ?").run(12345, created.user.id);
    db.prepare("UPDATE user SET role = ? WHERE id = ?").run("admin", created.user.id);
    assert.equal(db.prepare("SELECT role FROM user WHERE id = ?").get(ownAccounts[0].userId).role, "user");
    assert.equal(db.prepare("SELECT balanceSatang FROM user WHERE id = ?").get(ownAccounts[0].userId).balanceSatang, 0);
    // Seed provider records only; real OAuth authorization requires provider credentials.
    const ids = [crypto.randomUUID(), crypto.randomUUID()];
    for (const [index, provider] of ["google", "discord"].entries()) {
      db.prepare('INSERT INTO account (id, accountId, providerId, userId, createdAt, updatedAt) VALUES (?, ?, ?, ?, ?, ?)')
        .run(ids[index], `${provider}_${suffix}`, provider, created.user.id, Date.now(), Date.now());
    }
    db.close();
    assert.equal((await request("/unlink-account", { accountId: ids[0] })).status, 400);
    cookie = ownCookie;
    const linked = await (await request("/list-accounts")).json();
    assert.deepEqual(linked.map(a => a.providerId).sort(), ["credential", "discord", "google"]);
    for (const id of ids) assert.equal((await request("/unlink-account", { accountId: id })).status, 200);
    assert.equal((await (await request("/list-accounts")).json()).length, 1);
  }
  cookie = ownCookie;
  assert.equal((await request("/sign-out", {})).status, 200);
  assert.equal(await (await request("/get-session")).json(), null);
  const login = await request("/sign-in/username", { username: user.username.toUpperCase(), password: user.password }, false);
  assert.equal(login.status, 200, await login.clone().text());
  cookie = login.headers.getSetCookie().map(c => c.split(";")[0]).join("; ");
  const restored = await (await request("/get-session")).json();
  assert.equal(restored.user.id, created.user.id);
  assert.equal(restored.user.role, process.env.TEST_AUTH_DB ? "admin" : "user");
  await request("/update-user", { role: "user" });
  assert.equal((await (await request("/get-session")).json()).user.role, process.env.TEST_AUTH_DB ? "admin" : "user");
  assert.equal(restored.user.image, created.user.image);
  assert.equal(restored.user.balanceSatang, process.env.TEST_AUTH_DB ? 12345 : 0);
  if (process.env.TEST_AUTH_DB) {
    await request("/update-user", { balanceSatang: 0 });
    assert.equal((await (await request("/get-session")).json()).user.balanceSatang, 12345);
  }
  for (const page of ["/account", "/my-machines"]) {
    const res = await fetch(`${base}${page}`, { redirect: "manual" });
    const html = await res.text();
    assert.ok([307, 308].includes(res.status) || html.includes("NEXT_REDIRECT"), `${page} must redirect guests`);
  }
});
