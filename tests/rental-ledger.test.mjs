import assert from "node:assert/strict";
import { test } from "node:test";
import Database from "better-sqlite3";

function fixture(balanceSatang = 100_000) {
  const db = new Database(":memory:");
  db.pragma("foreign_keys = ON");
  db.exec(
    "CREATE TABLE user(id TEXT PRIMARY KEY, balanceSatang INTEGER NOT NULL DEFAULT 0, updatedAt TEXT);",
  );
  db.prepare("INSERT INTO user(id, balanceSatang, updatedAt) VALUES (?, ?, ?)").run(
    "renter",
    balanceSatang,
    new Date(0).toISOString(),
  );
  return db;
}

async function ledger() {
  return import("../src/lib/rental-payments.ts");
}

const request = (suffix = "") => crypto.randomUUID() + suffix;

function balance(db) {
  return db.prepare("SELECT balanceSatang FROM user WHERE id='renter'").get().balanceSatang;
}

function paymentCount(db) {
  return db.prepare("SELECT count(*) AS count FROM rental_payments").get().count;
}

function createPaid(db, api, options = {}) {
  const charged = api.chargeRental(db, {
    userId: "renter",
    machineId: "pc-1",
    machineName: "PC ONE",
    hourlyPriceSatang: 4000,
    hours: options.hours ?? 1,
    requestId: options.requestId ?? request(),
    now: options.now,
  });
  const payment = options.pending
    ? charged.payment
    : api.confirmRentalPayment(db, charged.payment.id, options.now);
  return { payment, charged };
}

function createConfirmed(db, api, options = {}) {
  const charged = api.chargeRental(db, {
    userId: "renter",
    machineId: "pc-1",
    machineName: "PC ONE",
    hourlyPriceSatang: options.hourlyPriceSatang ?? 4000,
    hours: options.hours ?? 1,
    requestId: options.requestId ?? request(),
    now: options.now,
  });
  return api.confirmRentalPayment(db, charged.payment.id, options.now);
}

test("rental charge debits a whole-baht amount and is idempotent per request id", async () => {
  const api = await ledger();
  const db = fixture();
  try {
    const requestId = request();
    const first = api.chargeRental(db, {
      userId: "renter",
      machineId: "pc-1",
      machineName: "PC ONE",
      hourlyPriceSatang: 4000,
      hours: 2,
      requestId,
    });
    assert.equal(first.payment.amountSatang, 8000);
    assert.equal(first.payment.durationSeconds, 7200);
    assert.equal(first.payment.status, "pending");
    assert.equal(first.balanceSatang, 92_000);
    assert.equal(balance(db), 92_000);
    assert.equal(paymentCount(db), 1);

    const repeated = api.chargeRental(db, {
      userId: "renter",
      machineId: "pc-1",
      machineName: "PC ONE",
      hourlyPriceSatang: 4000,
      hours: 2,
      requestId,
    });
    assert.equal(repeated.repeated, true);
    assert.equal(repeated.payment.id, first.payment.id);
    assert.equal(balance(db), 92_000);
    assert.equal(paymentCount(db), 1);

    assert.throws(
      () =>
        api.chargeRental(db, {
          userId: "renter",
          machineId: "pc-1",
          machineName: "PC ONE",
          hourlyPriceSatang: 4000,
          hours: 3,
          requestId,
        }),
      /รหัสคำขอซ้ำ/,
    );
  } finally {
    db.close();
  }
});

test("rental charge rejects fractional prices, partial hours and insufficient balance", async () => {
  const api = await ledger();
  const db = fixture(1000);
  try {
    assert.throws(
      () =>
        api.chargeRental(db, {
          userId: "renter",
          machineId: "pc-1",
          machineName: "PC ONE",
          hourlyPriceSatang: 4250,
          hours: 1,
          requestId: request(),
        }),
      /จำนวนเต็มบาท/,
    );
    assert.throws(
      () =>
        api.chargeRental(db, {
          userId: "renter",
          machineId: "pc-1",
          machineName: "PC ONE",
          hourlyPriceSatang: 4000,
          hours: 1.5,
          requestId: request(),
        }),
      /จำนวนชั่วโมง/,
    );
    let insufficient;
    assert.throws(
      () =>
        api.chargeRental(db, {
          userId: "renter",
          machineId: "pc-1",
          machineName: "PC ONE",
          hourlyPriceSatang: 4000,
          hours: 1,
          requestId: request(),
        }),
      (error) => {
        insufficient = error;
        return true;
      },
    );
    assert.equal(insufficient.code, "insufficient-funds");
    assert.equal(insufficient.status, 402);
    assert.equal(balance(db), 1000);
    assert.equal(paymentCount(db), 0);
  } finally {
    db.close();
  }
});

test("rejected bookings refund in full and cannot be refunded twice", async () => {
  const api = await ledger();
  const db = fixture();
  try {
    const { payment } = createPaid(db, api, { hours: 2, pending: true });
    assert.equal(balance(db), 92_000);
    const rejected = api.rejectAndRefundRentalPayment(db, payment.id, "เครื่องไม่ว่าง", 1234);
    assert.equal(rejected.status, "rejected");
    assert.equal(rejected.refundedSatang, 8000);
    assert.equal(balance(db), 100_000);
    const again = api.rejectAndRefundRentalPayment(db, payment.id, "ซ้ำ", 9999);
    assert.equal(again.refundedSatang, 8000);
    assert.equal(balance(db), 100_000);
  } finally {
    db.close();
  }
});

test("extension charges are idempotent and released with a full refund", async () => {
  const api = await ledger();
  const db = fixture();
  try {
    const { payment } = createPaid(db, api, { hours: 1 });
    const key = request();
    const extension = api.chargeRentalExtension(db, {
      userId: "renter",
      paymentId: payment.id,
      hours: 2,
      requestKey: key,
    });
    assert.equal(extension.payment.extensionSeconds, 7200);
    assert.equal(extension.payment.extensionSatang, 8000);
    assert.equal(extension.balanceSatang, 88_000);

    const repeated = api.chargeRentalExtension(db, {
      userId: "renter",
      paymentId: payment.id,
      hours: 2,
      requestKey: key,
    });
    assert.equal(repeated.repeated, true);
    assert.equal(balance(db), 88_000);

    const released = api.releaseRentalExtension(db, {
      userId: "renter",
      paymentId: payment.id,
      requestKey: key,
    });
    assert.equal(released.extensionSeconds, 0);
    assert.equal(released.extensionSatang, 0);
    assert.equal(balance(db), 96_000);
    api.releaseRentalExtension(db, {
      userId: "renter",
      paymentId: payment.id,
      requestKey: key,
    });
    assert.equal(balance(db), 96_000);
  } finally {
    db.close();
  }
});

test("unused time is refunded by the second and rounded half-up to satang", async () => {
  const api = await ledger();
  const db = fixture();
  try {
    const startsAt = Date.parse("2026-11-18T03:00:00.000Z");
    const { payment } = createPaid(db, api, { hours: 2 });
    assert.equal(balance(db), 92_000);

    const settled = api.settleRentalRefunds(
      db,
      [
        {
          id: payment.id,
          state: "completed",
          startsAtUtc: new Date(startsAt).toISOString(),
          endedAtUtc: new Date(startsAt + 80 * 60 * 1000).toISOString(),
        },
      ],
      { now: startsAt + 90 * 60 * 1000 },
    );
    // 2h paid = 8000 satang; 40 of 120 minutes unused -> 8000 * 2400 / 7200
    // = 2666.67 satang, rounded half-up to 2667 satang (never whole baht).
    assert.equal(settled.refundedSatang, 2667);
    assert.equal(settled.refundedPayments, 1);
    assert.equal(balance(db), 94_667);
    const stored = api.getRentalPayment(db, payment.id);
    assert.equal(stored.refundedSatang, 2667);
    assert.ok(stored.settledAt);
    assert.match(stored.refundNote, /เวลาที่ไม่ได้ใช้/);

    const again = api.settleRentalRefunds(
      db,
      [
        {
          id: payment.id,
          state: "completed",
          startsAtUtc: new Date(startsAt).toISOString(),
          endedAtUtc: new Date(startsAt + 80 * 60 * 1000).toISOString(),
        },
      ],
      { now: startsAt + 100 * 60 * 1000 },
    );
    assert.equal(again.refundedSatang, 0);
    assert.equal(balance(db), 94_667);
  } finally {
    db.close();
  }
});

test("an exact half-satang remainder rounds up while anything below rounds down", async () => {
  const api = await ledger();
  const db = fixture();
  try {
    const startsAt = Date.parse("2026-11-18T03:00:00.000Z");
    // 1h paid = 100 satang. Leaving 18 of 3600 seconds unused is exactly
    // 100 * 18000 / 3600000 = 0.5 satang, which must round up to 1 satang.
    const half = createConfirmed(db, api, { hourlyPriceSatang: 100, hours: 1 });
    const settledHalf = api.settleRentalRefunds(
      db,
      [
        {
          id: half.id,
          state: "ending",
          startsAtUtc: new Date(startsAt).toISOString(),
          endedAtUtc: new Date(startsAt + 3_582_000).toISOString(),
        },
      ],
      { now: startsAt + 3_600_000 },
    );
    assert.equal(settledHalf.refundedSatang, 1);
    assert.equal(balance(db), 99_901);

    // 17,999 unused milliseconds is just under half a satang and rounds down to 0.
    const below = createConfirmed(db, api, { hourlyPriceSatang: 100, hours: 1 });
    const settledBelow = api.settleRentalRefunds(
      db,
      [
        {
          id: below.id,
          state: "completed",
          startsAtUtc: new Date(startsAt).toISOString(),
          endedAtUtc: new Date(startsAt + 3_582_001).toISOString(),
        },
      ],
      { now: startsAt + 3_600_000 },
    );
    assert.equal(settledBelow.refundedSatang, 0);
    assert.equal(balance(db), 99_801);
    const stored = api.getRentalPayment(db, below.id);
    assert.equal(stored.refundedSatang, 0);
    assert.ok(stored.settledAt);
  } finally {
    db.close();
  }
});

test("refunds never exceed the paid amount and natural expiry refunds nothing", async () => {
  const api = await ledger();
  const db = fixture();
  try {
    const startsAt = Date.parse("2026-11-18T03:00:00.000Z");
    const { payment } = createPaid(db, api, { hours: 1 });
    const natural = api.settleRentalRefunds(
      db,
      [
        {
          id: payment.id,
          state: "completed",
          startsAtUtc: new Date(startsAt).toISOString(),
          endedAtUtc: new Date(startsAt + 60 * 60 * 1000).toISOString(),
        },
      ],
      { now: startsAt + 61 * 60 * 1000 },
    );
    assert.equal(natural.refundedSatang, 0);
    assert.equal(api.getRentalPayment(db, payment.id).settledAt !== null, true);

    const second = createPaid(db, api, { hours: 1 });
    // An administrator granted free extra time beyond the paid window.
    const freeTime = api.settleRentalRefunds(
      db,
      [
        {
          id: second.payment.id,
          state: "completed",
          startsAtUtc: new Date(startsAt).toISOString(),
          endedAtUtc: new Date(startsAt + 3 * 60 * 60 * 1000).toISOString(),
        },
      ],
      { now: startsAt + 4 * 60 * 60 * 1000 },
    );
    assert.equal(freeTime.refundedSatang, 0);
    assert.equal(balance(db), 92_000);
  } finally {
    db.close();
  }
});

test("bookings that never started are refunded in full including extensions", async () => {
  const api = await ledger();
  const db = fixture();
  try {
    const { payment } = createPaid(db, api, { hours: 3 });
    assert.equal(balance(db), 88_000);
    const settled = api.settleRentalRefunds(
      db,
      [{ id: payment.id, state: "failed", startsAtUtc: null, endedAtUtc: new Date().toISOString() }],
      { now: Date.now() },
    );
    assert.equal(settled.refundedSatang, 12_000);
    assert.equal(balance(db), 100_000);
    const stored = api.getRentalPayment(db, payment.id);
    assert.match(stored.refundNote, /คืนเต็มจำนวน/);

    // A lease that failed before starting and predates the ended_at migration has
    // no end instant either; it still never became active, so it refunds in full.
    const legacy = createConfirmed(db, api, { hours: 1 });
    const legacySettled = api.settleRentalRefunds(
      db,
      [{ id: legacy.id, state: "failed", startsAtUtc: null, endedAtUtc: null }],
      { now: Date.now() },
    );
    assert.equal(legacySettled.refundedSatang, 4000);
    assert.equal(api.getRentalPayment(db, legacy.id).settledAt !== null, true);

    // Extension satang is part of the paid amount, so it is refunded too.
    const extended = createConfirmed(db, api, { hours: 1 });
    api.chargeRentalExtension(db, {
      userId: "renter",
      paymentId: extended.id,
      hours: 1,
      requestKey: request(),
    });
    assert.equal(balance(db), 92_000);
    const extendedSettled = api.settleRentalRefunds(
      db,
      [{ id: extended.id, state: "failed", startsAtUtc: null, endedAtUtc: null }],
      { now: Date.now() },
    );
    assert.equal(extendedSettled.refundedSatang, 8000);
    assert.equal(balance(db), 100_000);
  } finally {
    db.close();
  }
});

test("a lease that ends before it becomes active is refunded in full", async () => {
  const api = await ledger();
  const db = fixture();
  try {
    const startsAt = Date.parse("2026-11-18T03:00:00.000Z");
    const { payment } = createPaid(db, api, { hours: 2 });
    assert.equal(balance(db), 92_000);
    const settled = api.settleRentalRefunds(
      db,
      [
        {
          id: payment.id,
          state: "completed",
          startsAtUtc: new Date(startsAt).toISOString(),
          // Cancelled ten minutes before the scheduled start: nothing was used.
          endedAtUtc: new Date(startsAt - 10 * 60 * 1000).toISOString(),
        },
      ],
      { now: startsAt + 60 * 1000 },
    );
    assert.equal(settled.refundedSatang, 8000);
    assert.equal(balance(db), 100_000);
    const stored = api.getRentalPayment(db, payment.id);
    assert.equal(stored.refundedSatang, 8000);
    assert.match(stored.refundNote, /คืนเต็มจำนวน/);
    assert.ok(stored.settledAt);
  } finally {
    db.close();
  }
});

test("extension time is prorated with the initial booking down to satang", async () => {
  const api = await ledger();
  const db = fixture();
  try {
    const startsAt = Date.parse("2026-11-18T03:00:00.000Z");
    const { payment } = createPaid(db, api, { hours: 1 });
    api.chargeRentalExtension(db, {
      userId: "renter",
      paymentId: payment.id,
      hours: 1,
      requestKey: request(),
    });
    assert.equal(balance(db), 92_000);
    assert.equal(api.getRentalPayment(db, payment.id).extensionSatang, 4000);
    const settled = api.settleRentalRefunds(
      db,
      [
        {
          id: payment.id,
          state: "ending",
          startsAtUtc: new Date(startsAt).toISOString(),
          endedAtUtc: new Date(startsAt + 70 * 60 * 1000).toISOString(),
        },
      ],
      { now: startsAt + 75 * 60 * 1000 },
    );
    // 2h paid = 4000 base + 4000 extension = 8000 satang; 50 of 120 minutes
    // unused -> 8000 * 3000 / 7200 = 3333.33 satang -> 3333 satang half-up.
    assert.equal(settled.refundedSatang, 3333);
    assert.equal(balance(db), 95_333);
    const stored = api.getRentalPayment(db, payment.id);
    assert.equal(stored.refundedSatang, 3333);
    assert.equal(stored.extensionSatang, 4000);
  } finally {
    db.close();
  }
});

test("refunds are capped by the not-yet-refunded paid amount and settle only once", async () => {
  const api = await ledger();
  const db = fixture();
  try {
    const startsAt = Date.parse("2026-11-18T03:00:00.000Z");
    // Ending 1ms after the start leaves virtually the whole hour unused. The
    // half-up result rounds to exactly the paid amount and never above it.
    const almost = createConfirmed(db, api, { hourlyPriceSatang: 1000, hours: 1 });
    assert.equal(balance(db), 99_000);
    const settled = api.settleRentalRefunds(
      db,
      [
        {
          id: almost.id,
          state: "completed",
          startsAtUtc: new Date(startsAt).toISOString(),
          endedAtUtc: new Date(startsAt + 1).toISOString(),
        },
      ],
      { now: startsAt + 60 * 60 * 1000 },
    );
    assert.equal(settled.refundedSatang, 1000);
    assert.equal(balance(db), 100_000);
    assert.equal(api.getRentalPayment(db, almost.id).refundedSatang, 1000);

    const noDouble = api.settleRentalRefunds(
      db,
      [
        {
          id: almost.id,
          state: "completed",
          startsAtUtc: new Date(startsAt).toISOString(),
          endedAtUtc: new Date(startsAt + 1).toISOString(),
        },
      ],
      { now: startsAt + 2 * 60 * 60 * 1000 },
    );
    assert.equal(noDouble.refundedSatang, 0);
    assert.equal(balance(db), 100_000);

    // A payment that already carries a partial refund can only receive the
    // remainder: paid 1000 - already refunded 800 = at most 200 more satang.
    const partial = createConfirmed(db, api, { hourlyPriceSatang: 1000, hours: 1 });
    db.prepare("UPDATE rental_payments SET refunded_satang = 800 WHERE id = ?").run(partial.id);
    const capped = api.settleRentalRefunds(
      db,
      [
        {
          id: partial.id,
          state: "completed",
          startsAtUtc: new Date(startsAt).toISOString(),
          endedAtUtc: new Date(startsAt + 30 * 60 * 1000).toISOString(),
        },
      ],
      { now: startsAt + 60 * 60 * 1000 },
    );
    // The half-hour remainder computes 500 satang but only 200 is refundable.
    assert.equal(capped.refundedSatang, 200);
    const stored = api.getRentalPayment(db, partial.id);
    assert.equal(stored.refundedSatang, 1000);
    assert.ok(stored.settledAt);
    assert.equal(balance(db), 99_200);
  } finally {
    db.close();
  }
});

test("payment summary and member balance expose the money ledger", async () => {
  const api = await ledger();
  const db = fixture();
  try {
    const { payment } = createPaid(db, api, { hours: 2 });
    const expansion = createPaid(db, api, { hours: 1, pending: true });
    api.rejectAndRefundRentalPayment(db, expansion.payment.id, "ทดสอบ");
    api.settleRentalRefunds(
      db,
      [{ id: payment.id, state: "failed", startsAtUtc: null, endedAtUtc: new Date().toISOString() }],
      { now: Date.now() },
    );
    const summary = api.rentalPaymentSummary(db);
    assert.equal(summary.totalRentals, 1);
    assert.equal(summary.revenueSatang, 0);
    assert.equal(summary.refundedSatang, 8000);
    assert.equal(api.getMemberBalance(db, "renter"), 100_000);
    assert.equal(api.listRentalPayments(db, "renter").length, 2);
  } finally {
    db.close();
  }
});
