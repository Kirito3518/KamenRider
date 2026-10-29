import assert from "node:assert/strict";
import { test } from "node:test";
import Database from "better-sqlite3";

function fixture() {
  const db = new Database(":memory:");
  db.exec(`CREATE TABLE user(id TEXT PRIMARY KEY, name TEXT, email TEXT, username TEXT, image TEXT, role TEXT, balanceSatang INTEGER, createdAt INTEGER, updatedAt INTEGER, emailVerified INTEGER);
    CREATE TABLE account(id TEXT PRIMARY KEY, userId TEXT, providerId TEXT, accountId TEXT, createdAt INTEGER);`);
  const add = db.prepare("INSERT INTO user VALUES (?, ?, ?, ?, NULL, ?, ?, ?, ?, 0)");
  const now = Date.parse("2026-10-29T05:00:00Z");
  add.run("admin", "Admin", "admin@example.com", "admin", "admin", 0, now, now);
  add.run("alice", "Alice", "alice@example.com", "alice", "user", 1000, now, now);
  add.run("bob", "Bob", "bob@example.com", "bob", "user", 2000, Date.parse("2026-09-30T18:00:00Z"), now);
  db.prepare("INSERT INTO account VALUES ('a', 'alice', 'google', 'google-123', ?)").run(now);
  db.prepare("INSERT INTO account VALUES ('b', 'alice', 'discord', 'discord-123', ?)").run(now);
  return { db, now };
}

test("member queries use Bangkok dates, distinct social counts and escaped search", async () => {
  const { readAdminMembers } = await import("../src/lib/admin-members-data.ts");
  const { db, now } = fixture();
  db.prepare("UPDATE user SET createdAt = ? WHERE id = 'alice'").run(new Date(now).toISOString());
  try {
    const data = readAdminMembers(db, { period: "month" }, now);
    assert.equal(data.stats.totalMembers, 3);
    assert.equal(data.stats.newMembers, 3);
    assert.equal(data.stats.linkedMembers, 1);
    assert.equal(data.stats.totalBalanceSatang, 3000);
    assert.equal(typeof data.members.find(member => member.id === "alice").createdAt, "number");
    assert.equal(data.chart.reduce((sum, bucket) => sum + bucket.count, 0), 3);
    assert.equal(data.chart[0].count, 1, "Oct 1 in Bangkok must include Sep 30 18:00 UTC");
    assert.equal(readAdminMembers(db, { q: "%" }, now).total, 0);
    assert.equal(readAdminMembers(db, { q: "GOOGLE-123", provider: "google", role: "user" }, now).total, 1);
    assert.equal(readAdminMembers(db, { linked: "unlinked" }, now).total, 2);
    assert.equal(readAdminMembers(db, { role: "admin" }, now).members[0].id, "admin");
    assert.equal(readAdminMembers(db, { page: "999" }, now).page, 1);
    assert.ok(readAdminMembers(db, { period: "custom", from: "2026-10-30", to: "2026-10-01" }, now).rangeError);
  } finally { db.close(); }
});

test("admin mutations are audited, idempotent and enforce balance and role invariants", async () => {
  const { applyMemberChange } = await import("../src/lib/admin-member-changes.ts");
  const { db } = fixture();
  const change = (data, target = "alice", actor = "admin") => applyMemberChange(db, actor, target, data);
  const credit = { requestId: crypto.randomUUID(), action: "credit", amount: "12.34", reason: "ปรับยอดทดสอบ" };
  try {
    assert.equal(change(credit).balanceSatang, 2234);
    assert.equal(change(credit).balanceSatang, 2234);
    assert.equal(db.prepare("SELECT count(*) AS count FROM adminMemberAudit").get().count, 1);
    assert.throws(() => change({ ...credit, amount: "99.00" }), /ซ้ำ/);
    assert.throws(() => change({ ...credit, requestId: crypto.randomUUID(), action: "debit", amount: "99" }), /ไม่เพียงพอ/);
    assert.throws(() => change({ ...credit, requestId: crypto.randomUUID(), amount: "0.001" }), /จำนวนเงิน/);
    assert.throws(() => change({ ...credit, requestId: crypto.randomUUID() }, "alice", "bob"), /สิทธิ์/);
    assert.throws(() => change({ requestId: crypto.randomUUID(), action: "role", role: "user", reason: "ลดสิทธิ์ตนเอง" }, "admin"), /ตัวเอง/);
    assert.equal(change({ requestId: crypto.randomUUID(), action: "role", role: "admin", reason: "เพิ่มผู้ดูแล" }).role, "admin");
    db.prepare("UPDATE user SET role='user' WHERE id='admin'").run();
    assert.throws(() => change({ ...credit, requestId: crypto.randomUUID() }), /สิทธิ์/);
  } finally { db.close(); }
});
