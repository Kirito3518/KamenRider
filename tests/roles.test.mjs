import assert from "node:assert/strict";
import { test } from "node:test";
import Database from "better-sqlite3";
import { getMigrations } from "better-auth/db/migration";
import { roleField } from "../src/lib/auth-schema.ts";

test("role migration fills existing members and remains safe to repeat", async () => {
  const database = new Database(":memory:");
  try {
    const baseline = { database };
    await (await getMigrations(baseline)).runMigrations();
    database.prepare('INSERT INTO user (id, name, email, emailVerified, createdAt, updatedAt) VALUES (?, ?, ?, ?, ?, ?)').run("existing", "Existing member", "existing@example.com", 0, Date.now(), Date.now());
    const options = { ...baseline, user: { additionalFields: { role: roleField } } };
    await (await getMigrations(options)).runMigrations();
    assert.equal(database.prepare('SELECT role FROM user WHERE id = ?').get("existing").role, "user");
    database.prepare('UPDATE user SET role = ? WHERE id = ?').run("admin", "existing");
    await (await getMigrations(options)).runMigrations();
    assert.equal(database.prepare('SELECT role FROM user WHERE id = ?').get("existing").role, "admin");
  } finally { database.close(); }
});
