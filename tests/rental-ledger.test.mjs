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

test("unused time is refunded by the second when a lease ends early", async () => {
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
    // 2h paid = 8000 satang; 40 of 120 minutes unused -> 2666 satang.
    assert.equal(settled.refundedSatang, 2666);
    assert.equal(settled.refundedPayments, 1);
    assert.equal(balance(db), 94_666);
    const stored = api.getRentalPayment(db, payment.id);
    assert.equal(stored.refundedSatang, 2666);
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
    assert.equal(balance(db), 94_666);
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

test("bookings that never started are refunded in full", async () => {
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
  } finally {
    db.close();
  }
});

test("extension time is prorated with the initial booking", async () => {
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
    const settled = api.settleRentalRefunds(
      db,
      [
        {
          id: payment.id,
          state: "ending",
          startsAtUtc: new Date(startsAt).toISOString(),
          endedAtUtc: new Date(startsAt + 90 * 60 * 1000).toISOString(),
        },
      ],
      { now: startsAt + 95 * 60 * 1000 },
    );
    // 2h paid = 8000 satang; 30 of 120 minutes unused -> 2000 satang.
    assert.equal(settled.refundedSatang, 2000);
    assert.equal(balance(db), 94_000);
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
