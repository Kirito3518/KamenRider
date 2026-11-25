import assert from "node:assert/strict";
import { register } from "node:module";
import { join } from "node:path";
import { test } from "node:test";
import { pathToFileURL } from "node:url";
import Database from "better-sqlite3";

/**
 * In-memory SQLite tests for the `/admin/overview` data helpers.
 *
 * The helpers live in TypeScript modules that use the `@/` path alias and import
 * `next/server` / `next/headers` through the auth/session modules. Plain
 * `node --test` cannot resolve either, so this file registers a tiny module hook:
 * `@/` resolves to `src/`, `next/*` resolves to an inert stub (no auth call
 * happens in these tests), and TypeScript is transformed with Node's built-in
 * transform mode (strip-only mode rejects the parameter properties used by the
 * Controller client). Production code is untouched.
 */

const sourceRoot = pathToFileURL(join(process.cwd(), "src") + "/").href;
const stubSource = `export const connection = async () => {};
export const headers = async () => new Headers();
export const redirect = () => { throw new Error("redirect() is not available in unit tests"); };
export default {};`;
const stubUrl = "data:text/javascript," + encodeURIComponent(stubSource);

const hookSource = `
import { existsSync, readFileSync } from "node:fs";
import { stripTypeScriptTypes } from "node:module";
import { fileURLToPath } from "node:url";
const sourceRoot = ${JSON.stringify(sourceRoot)};
const stubUrl = ${JSON.stringify(stubUrl)};
const stubs = new Set(["next/server", "next/headers", "next/navigation", "server-only"]);
const extensions = [".ts", ".tsx"];
function withExtension(url) {
  if (/\\.(ts|tsx|mjs|cjs|js|json)$/.test(url)) return url;
  for (const suffix of extensions) {
    const candidate = url + suffix;
    try { if (existsSync(new URL(candidate))) return candidate; } catch {}
  }
  return url;
}
export async function resolve(specifier, context, nextResolve) {
  if (specifier.startsWith("@/")) {
    return nextResolve(withExtension(new URL(specifier.slice(2), sourceRoot).href), context);
  }
  if (stubs.has(specifier)) return nextResolve(stubUrl, context);
  if (specifier.startsWith(".") && context.parentURL) {
    const base = new URL(specifier, context.parentURL).href;
    const resolved = withExtension(base);
    if (resolved !== base) return nextResolve(resolved, context);
  }
  return nextResolve(specifier, context);
}
export async function load(url, context, nextLoad) {
  if (url.startsWith("file:") && url.endsWith(".ts")) {
    const source = readFileSync(fileURLToPath(url), "utf8");
    const transformed = stripTypeScriptTypes(source, { mode: "transform", sourceUrl: url });
    return { format: "module", source: transformed, shortCircuit: true };
  }
  return nextLoad(url, context);
}
`;
register("data:text/javascript," + encodeURIComponent(hookSource));

const { readAdminMembers } = await import("../src/lib/admin-members-data.ts");
const { readMemberTotals } = await import("../src/lib/admin.ts");
const { listAdminTopups, readTopupTotals } = await import("../src/lib/topup/history.ts");
const { readRentalRangeTotals, readRecentRentalPayments } = await import(
  "../src/lib/admin-overview.ts"
);
const { rentalPaymentSummary, ensureRentalSchema } = await import("../src/lib/rental-payments.ts");
const { ensureTopupSchema } = await import("../src/lib/topup/schema.ts");

const DAY = 86_400_000;

function fixture() {
  const db = new Database(":memory:");
  db.exec(`
    CREATE TABLE user(id TEXT PRIMARY KEY, name TEXT, email TEXT, username TEXT, image TEXT, role TEXT, balanceSatang INTEGER, createdAt INTEGER, updatedAt INTEGER, emailVerified INTEGER);
    CREATE TABLE account(id TEXT PRIMARY KEY, userId TEXT, providerId TEXT, accountId TEXT, createdAt INTEGER);
  `);
  ensureTopupSchema(db);
  ensureRentalSchema(db);
  return db;
}

function addUser(db, { id, name, email, role = "user", balanceSatang = 0, createdAt }) {
  db.prepare("INSERT INTO user VALUES (?, ?, ?, ?, NULL, ?, ?, ?, ?, 0)").run(
    id,
    name,
    email,
    id,
    role,
    balanceSatang,
    createdAt,
    createdAt,
  );
}

function addTopup(db, { id, status, requestedSatang, creditedSatang = 0, createdAt, completedAt = null, userId = "u1" }) {
  db.prepare(
    `INSERT INTO topup_transactions
       (id, reference, user_id, user_name, user_email, channel, status, requested_satang,
        credited_satang, idempotency_key, request_fingerprint, created_at, updated_at, completed_at)
     VALUES (?, ?, ?, ?, ?, 'promptpay', ?, ?, ?, ?, ?, ?, ?, ?)`,
  ).run(
    id,
    `REF-${id}`,
    userId,
    "Topup User",
    "topup@example.com",
    status,
    requestedSatang,
    creditedSatang,
    `idem-${id}`,
    `fingerprint-${id}`,
    createdAt,
    createdAt,
    completedAt,
  );
}

function addRental(db, { id, userId, status, amountSatang, extensionSatang = 0, refundedSatang = 0, createdAt }) {
  db.prepare(
    `INSERT INTO rental_payments
       (id, user_id, machine_id, machine_name, hourly_price_satang, duration_seconds, amount_satang,
        extension_seconds, extension_satang, refunded_satang, status, reject_reason, refunded_at,
        refund_note, settled_at, created_at, updated_at)
     VALUES (?, ?, 'pc-1', 'PC ONE', 10000, 3600, ?, 0, ?, ?, ?, NULL, NULL, NULL, NULL, ?, ?)`,
  ).run(id, userId, amountSatang, extensionSatang, refundedSatang, status, createdAt, createdAt);
}

function bangkokMidnight(dateKey) {
  return Date.parse(`${dateKey}T00:00:00+07:00`);
}

test("member counts and new sign-ups use Bangkok-day boundaries", async () => {
  const db = fixture();
  const now = Date.parse("2026-10-29T05:00:00Z"); // 2026-10-29 12:00 Bangkok
  try {
    addUser(db, { id: "admin", name: "Admin", email: "admin@example.com", role: "admin", balanceSatang: 0, createdAt: Date.parse("2026-09-30T18:00:00Z") }); // Oct 1 01:00 Bangkok
    addUser(db, { id: "alice", name: "Alice", email: "alice@example.com", balanceSatang: 1000, createdAt: Date.parse("2026-10-31T16:59:00Z") }); // Oct 31 23:59 Bangkok
    addUser(db, { id: "bob", name: "Bob", email: "bob@example.com", balanceSatang: 2000, createdAt: Date.parse("2026-10-31T17:00:00Z") }); // Nov 1 00:00 Bangkok
    addUser(db, { id: "carol", name: "Carol", email: "carol@example.com", balanceSatang: 5000, createdAt: Date.parse("2025-12-31T17:00:00Z") });
    addUser(db, { id: "dave", name: "Dave", email: "dave@example.com", balanceSatang: 0, createdAt: Date.parse("2026-09-30T17:00:00Z") }); // Oct 1 00:00 Bangkok (inclusive)

    const october = readAdminMembers(db, { period: "custom", from: "2026-10-01", to: "2026-10-31" }, now);
    assert.equal(october.stats.totalMembers, 5);
    assert.equal(october.stats.newMembers, 3, "UTC Sep 30 17:00/18:00 must count as Oct 1 in Bangkok");
    assert.equal(october.stats.previousMembers, 0);
    assert.equal(october.chart.reduce((total, bucket) => total + bucket.count, 0), 3);

    const november = readAdminMembers(db, { period: "custom", from: "2026-11-01", to: "2026-11-30" }, now);
    assert.equal(november.stats.newMembers, 1, "UTC Oct 31 17:00 is Nov 1 Bangkok");
    // The comparison window has the same length (30 days) and ends at Nov 1,
    // so it covers Oct 2–Oct 31: only Alice falls inside it.
    assert.equal(november.stats.previousMembers, 1);

    assert.deepEqual(readMemberTotals(db), {
      members: 5,
      admins: 1,
      totalBalanceSatang: 8000,
    });
  } finally {
    db.close();
  }
});

test("top-ups count creations and completions in their own windows", async () => {
  const db = fixture();
  const windowStart = bangkokMidnight("2026-01-01");
  const windowEnd = bangkokMidnight("2026-02-01");
  try {
    addTopup(db, { id: "t1", status: "completed", requestedSatang: 5000, creditedSatang: 5000, createdAt: windowStart + 4 * DAY, completedAt: windowStart + 19 * DAY });
    addTopup(db, { id: "t2", status: "completed", requestedSatang: 7000, creditedSatang: 7000, createdAt: windowStart + 5 * DAY, completedAt: windowEnd + 4 * DAY });
    addTopup(db, { id: "t3", status: "pending", requestedSatang: 3000, createdAt: windowStart + 6 * DAY });
    addTopup(db, { id: "t4", status: "processing", requestedSatang: 4000, createdAt: windowStart + 9 * DAY });
    addTopup(db, { id: "t5", status: "expired", requestedSatang: 1000, createdAt: windowStart - 60 * DAY });

    const data = listAdminTopups(db, { period: "custom", from: "2026-01-01", to: "2026-01-31", pageSize: "5" });
    assert.equal(data.stats.total, 4, "only top-ups created inside the window count as created");
    assert.equal(data.stats.completed, 1, "completion window only includes t1");
    assert.equal(data.stats.creditedSatang, 5000, "t2 completed outside the window is not credited here");
    assert.equal(data.stats.requestedSatang, 19000);
    assert.equal(data.stats.pending, 2);
    assert.equal(data.stats.failed, 0);
    assert.equal(data.previous.total, 0);
    assert.equal(data.entries.length, 4);
    const chartCount = data.chart.reduce((total, point) => total + point.promptpayCount, 0);
    assert.equal(chartCount, 4, "the chart counts creations in the window");

    assert.deepEqual(readTopupTotals(db), {
      total: 5,
      completed: 2,
      creditedSatang: 12000,
      requestedSatang: 20000,
    });
  } finally {
    db.close();
  }
});

test("rental money is read from confirmed payments without settling anything", async () => {
  const db = fixture();
  addUser(db, { id: "u1", name: "Alice", email: "alice@example.com", createdAt: Date.parse("2025-12-01T00:00:00Z") });
  const windowStart = bangkokMidnight("2026-01-01");
  const windowEnd = bangkokMidnight("2026-02-01");
  const previousStart = windowStart - (windowEnd - windowStart);
  try {
    addRental(db, { id: "r1", userId: "u1", status: "confirmed", amountSatang: 10000, extensionSatang: 2000, refundedSatang: 3000, createdAt: windowStart + 4 * DAY });
    addRental(db, { id: "r2", userId: "u1", status: "confirmed", amountSatang: 4000, createdAt: windowStart - 17 * DAY }); // previous window
    addRental(db, { id: "r3", userId: "u1", status: "pending", amountSatang: 5000, createdAt: windowStart + 5 * DAY });
    addRental(db, { id: "r4", userId: "u1", status: "rejected", amountSatang: 6000, refundedSatang: 6000, createdAt: windowStart + 6 * DAY });

    assert.deepEqual(readRentalRangeTotals(db, windowStart, windowEnd), {
      paidSatang: 12000,
      refundedSatang: 3000,
      netSatang: 9000,
      rentalCount: 1,
    });
    assert.deepEqual(readRentalRangeTotals(db, previousStart, windowStart), {
      paidSatang: 4000,
      refundedSatang: 0,
      netSatang: 4000,
      rentalCount: 1,
    });
    assert.deepEqual(rentalPaymentSummary(db), {
      totalRentals: 2,
      revenueSatang: 13000,
      refundedSatang: 3000,
    });

    const recent = readRecentRentalPayments(db, 5);
    assert.equal(recent.length, 4);
    assert.equal(recent[0].id, "r4", "newest payment first");
    assert.equal(recent[0].userName, "Alice");
    assert.equal(recent[0].userEmail, "alice@example.com");
  } finally {
    db.close();
  }
});
