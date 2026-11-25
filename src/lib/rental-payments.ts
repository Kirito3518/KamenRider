import type Database from "better-sqlite3";
import { randomUUID } from "node:crypto";

/**
 * Rental payment records live in the auth database (the same SQLite file as the
 * member accounts). The Controller owns lease state and time; this module owns
 * money: prepaid charges, extension charges and prorated refunds.
 *
 * Every balance mutation happens inside an IMMEDIATE transaction and is guarded
 * by a compare-and-set (`settled_at IS NULL` for refunds, conditional balance
 * update for debits) so concurrent requests cannot double-charge or double-refund.
 */

export const RENTAL_HOUR_SECONDS = 3_600;
export const MAX_RENTAL_HOURS = 24;

export type RentalPaymentStatus = "pending" | "confirmed" | "rejected";

export type RentalPayment = {
  id: string;
  userId: string;
  machineId: string;
  machineName: string;
  hourlyPriceSatang: number;
  durationSeconds: number;
  amountSatang: number;
  extensionSeconds: number;
  extensionSatang: number;
  refundedSatang: number;
  status: RentalPaymentStatus;
  rejectReason: string | null;
  refundedAt: number | null;
  refundNote: string | null;
  settledAt: number | null;
  createdAt: number;
  updatedAt: number;
};

export type RentalPaymentErrorCode =
  | "invalid-request"
  | "invalid-price"
  | "insufficient-funds"
  | "idempotency-conflict"
  | "payment-not-found"
  | "payment-not-ready"
  | "machine-unavailable"
  | "controller-unavailable"
  | "account-not-found"
  | "amount-overflow";

export class RentalPaymentError extends Error {
  readonly code: RentalPaymentErrorCode;
  readonly status: number;

  constructor(code: RentalPaymentErrorCode, message: string, status: number) {
    super(message);
    this.name = "RentalPaymentError";
    this.code = code;
    this.status = status;
  }
}

const PAYMENT_COLUMNS =
  "id,user_id,machine_id,machine_name,hourly_price_satang,duration_seconds,amount_satang," +
  "extension_seconds,extension_satang,refunded_satang,status,reject_reason,refunded_at,refund_note," +
  "settled_at,created_at,updated_at";

type PaymentRow = {
  id: string;
  user_id: string;
  machine_id: string;
  machine_name: string;
  hourly_price_satang: number;
  duration_seconds: number;
  amount_satang: number;
  extension_seconds: number;
  extension_satang: number;
  refunded_satang: number;
  status: RentalPaymentStatus;
  reject_reason: string | null;
  refunded_at: number | null;
  refund_note: string | null;
  settled_at: number | null;
  created_at: number;
  updated_at: number;
};

const schemaReady = new WeakSet<object>();

export function ensureRentalSchema(database: Database.Database) {
  if (schemaReady.has(database)) return;
  database.exec(`
    CREATE TABLE IF NOT EXISTS rental_payments (
      id TEXT PRIMARY KEY,
      user_id TEXT NOT NULL,
      machine_id TEXT NOT NULL,
      machine_name TEXT NOT NULL,
      hourly_price_satang INTEGER NOT NULL CHECK(hourly_price_satang >= 100 AND hourly_price_satang % 100 = 0),
      duration_seconds INTEGER NOT NULL CHECK(duration_seconds > 0 AND duration_seconds % 3600 = 0),
      amount_satang INTEGER NOT NULL CHECK(amount_satang >= 0),
      extension_seconds INTEGER NOT NULL DEFAULT 0 CHECK(extension_seconds >= 0 AND extension_seconds % 3600 = 0),
      extension_satang INTEGER NOT NULL DEFAULT 0 CHECK(extension_satang >= 0),
      refunded_satang INTEGER NOT NULL DEFAULT 0 CHECK(refunded_satang >= 0),
      status TEXT NOT NULL CHECK(status IN ('pending','confirmed','rejected')),
      reject_reason TEXT,
      refunded_at INTEGER,
      refund_note TEXT,
      settled_at INTEGER,
      created_at INTEGER NOT NULL,
      updated_at INTEGER NOT NULL
    );
    CREATE INDEX IF NOT EXISTS rental_payments_user ON rental_payments(user_id, created_at DESC);
    CREATE INDEX IF NOT EXISTS rental_payments_unsettled ON rental_payments(settled_at) WHERE settled_at IS NULL;

    CREATE TABLE IF NOT EXISTS rental_payment_requests (
      userId TEXT NOT NULL,
      requestKey TEXT NOT NULL,
      fingerprint TEXT NOT NULL,
      paymentId TEXT NOT NULL,
      PRIMARY KEY(userId, requestKey)
    );

    CREATE TABLE IF NOT EXISTS rental_extension_payments (
      id TEXT PRIMARY KEY,
      payment_id TEXT NOT NULL REFERENCES rental_payments(id),
      request_key TEXT NOT NULL,
      additional_seconds INTEGER NOT NULL CHECK(additional_seconds > 0 AND additional_seconds % 3600 = 0),
      amount_satang INTEGER NOT NULL CHECK(amount_satang >= 0),
      status TEXT NOT NULL CHECK(status IN ('charged','released')),
      created_at INTEGER NOT NULL,
      UNIQUE(payment_id, request_key)
    );
  `);
  schemaReady.add(database);
}

function toPayment(row: PaymentRow): RentalPayment {
  return {
    id: row.id,
    userId: row.user_id,
    machineId: row.machine_id,
    machineName: row.machine_name,
    hourlyPriceSatang: row.hourly_price_satang,
    durationSeconds: row.duration_seconds,
    amountSatang: row.amount_satang,
    extensionSeconds: row.extension_seconds,
    extensionSatang: row.extension_satang,
    refundedSatang: row.refunded_satang,
    status: row.status,
    rejectReason: row.reject_reason,
    refundedAt: row.refunded_at,
    refundNote: row.refund_note,
    settledAt: row.settled_at,
    createdAt: row.created_at,
    updatedAt: row.updated_at,
  };
}

function findPayment(database: Database.Database, paymentId: string) {
  const row = database
    .prepare(`SELECT ${PAYMENT_COLUMNS} FROM rental_payments WHERE id = ?`)
    .get(paymentId) as PaymentRow | undefined;
  return row ? toPayment(row) : null;
}

export function getRentalPayment(database: Database.Database, paymentId: string) {
  ensureRentalSchema(database);
  return findPayment(database, paymentId);
}

function clampText(value: unknown, maxLength: number) {
  if (typeof value !== "string") return "";
  return value.trim().slice(0, maxLength);
}

function assertIdentifier(value: unknown, label: string) {
  const text = clampText(value, 128);
  if (!text) throw new RentalPaymentError("invalid-request", `ข้อมูล${label}ไม่ถูกต้อง`, 400);
  return text;
}

function assertRequestKey(value: unknown) {
  const text = typeof value === "string" ? value.trim() : "";
  if (
    !/^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i.test(text)
  )
    throw new RentalPaymentError("invalid-request", "รหัสคำขอไม่ถูกต้อง", 400);
  return text;
}

export function assertRentalHours(value: unknown) {
  if (!Number.isSafeInteger(value) || (value as number) < 1 || (value as number) > MAX_RENTAL_HOURS)
    throw new RentalPaymentError(
      "invalid-request",
      `จำนวนชั่วโมงต้องเป็นจำนวนเต็ม 1–${MAX_RENTAL_HOURS} ชั่วโมง`,
      400,
    );
  return value as number;
}

export function assertHourlyPriceSatang(value: unknown) {
  if (
    !Number.isSafeInteger(value) ||
    (value as number) < 100 ||
    (value as number) % 100 !== 0
  )
    throw new RentalPaymentError(
      "invalid-price",
      "ราคาต่อชั่วโมงของเครื่องนี้ต้องเป็นจำนวนเต็มบาท กรุณาติดต่อผู้ดูแล",
      409,
    );
  return value as number;
}

function assertRentalAmount(value: number) {
  if (!Number.isSafeInteger(value) || value < 1)
    throw new RentalPaymentError("amount-overflow", "ยอดเงินค่าเช่าเกินขอบเขตที่ระบบรองรับ", 409);
  return value;
}

function readBalance(database: Database.Database, userId: string) {
  const row = database
    .prepare("SELECT balanceSatang FROM user WHERE id = ?")
    .get(userId) as { balanceSatang: number } | undefined;
  if (!row) throw new RentalPaymentError("account-not-found", "ไม่พบบัญชีสมาชิก", 409);
  if (!Number.isSafeInteger(row.balanceSatang))
    throw new RentalPaymentError("amount-overflow", "ยอดเงินในบัญชีไม่ถูกต้อง", 409);
  return row.balanceSatang;
}

function debit(database: Database.Database, userId: string, amount: number, now: number) {
  const current = readBalance(database, userId);
  if (current < amount)
    throw new RentalPaymentError(
      "insufficient-funds",
      "ยอดเงินไม่เพียงพอ กรุณาติดต่อผู้ดูแลเพื่อเติมเงิน",
      402,
    );
  const next = current - amount;
  const result = database
    .prepare("UPDATE user SET balanceSatang = ?, updatedAt = ? WHERE id = ? AND balanceSatang = ?")
    .run(next, new Date(now).toISOString(), userId, current);
  if (result.changes !== 1)
    throw new RentalPaymentError("insufficient-funds", "ยอดเงินไม่เพียงพอ กรุณาลองใหม่", 402);
  return next;
}

function credit(database: Database.Database, userId: string, amount: number, now: number) {
  if (amount <= 0) return readBalance(database, userId);
  const current = readBalance(database, userId);
  const next = current + amount;
  if (!Number.isSafeInteger(next))
    throw new RentalPaymentError("amount-overflow", "ยอดเงินเกินขอบเขตที่ระบบรองรับ", 409);
  database
    .prepare("UPDATE user SET balanceSatang = ?, updatedAt = ? WHERE id = ?")
    .run(next, new Date(now).toISOString(), userId);
  return next;
}

export type ChargeRentalInput = {
  userId: string;
  machineId: string;
  machineName: string;
  hourlyPriceSatang: number;
  hours: number;
  requestId: string;
  now?: number;
};

export type ChargeRentalResult = {
  payment: RentalPayment;
  balanceSatang: number;
  repeated: boolean;
};

/**
 * Charges the full booked duration up front and records the create request for
 * idempotent retries. The caller then creates the Controller lease with
 * `payment.id` as the idempotency key.
 */
export function chargeRental(database: Database.Database, input: ChargeRentalInput): ChargeRentalResult {
  ensureRentalSchema(database);
  const userId = assertIdentifier(input.userId, "บัญชี");
  const machineId = assertIdentifier(input.machineId, "เครื่อง");
  const machineName = clampText(input.machineName, 128) || machineId;
  const hours = assertRentalHours(input.hours);
  const price = assertHourlyPriceSatang(input.hourlyPriceSatang);
  const requestId = assertRequestKey(input.requestId);
  const amountSatang = assertRentalAmount(price * hours);
  const durationSeconds = hours * RENTAL_HOUR_SECONDS;
  const fingerprint = JSON.stringify({ machineId, hours });
  const now = input.now ?? Date.now();

  return database
    .transaction((): ChargeRentalResult => {
      const prior = database
        .prepare(
          "SELECT paymentId, fingerprint FROM rental_payment_requests WHERE userId = ? AND requestKey = ?",
        )
        .get(userId, requestId) as { paymentId: string; fingerprint: string } | undefined;
      if (prior) {
        if (prior.fingerprint !== fingerprint)
          throw new RentalPaymentError(
            "idempotency-conflict",
            "รหัสคำขอซ้ำกับรายการอื่น กรุณาเริ่มรายการใหม่",
            409,
          );
        const payment = findPayment(database, prior.paymentId);
        if (!payment) throw new RentalPaymentError("payment-not-found", "ไม่พบรายการชำระเงิน", 409);
        return { payment, balanceSatang: readBalance(database, userId), repeated: true };
      }

      const paymentId = randomUUID();
      database
        .prepare(
          `INSERT INTO rental_payments(${PAYMENT_COLUMNS})
           VALUES(?,?,?,?,?,?,?,0,0,0,'pending',NULL,NULL,NULL,NULL,?,?)`,
        )
        .run(paymentId, userId, machineId, machineName, price, durationSeconds, amountSatang, now, now);
      database
        .prepare(
          "INSERT INTO rental_payment_requests(userId, requestKey, fingerprint, paymentId) VALUES(?,?,?,?)",
        )
        .run(userId, requestId, fingerprint, paymentId);
      const balanceSatang = debit(database, userId, amountSatang, now);
      const payment = findPayment(database, paymentId);
      if (!payment) throw new RentalPaymentError("payment-not-found", "ไม่พบรายการชำระเงิน", 409);
      return { payment, balanceSatang, repeated: false };
    })
    .immediate();
}

/** Marks a payment as backed by a Controller lease. Idempotent. */
export function confirmRentalPayment(database: Database.Database, paymentId: string, now = Date.now()) {
  ensureRentalSchema(database);
  return database
    .transaction(() => {
      database
        .prepare(
          "UPDATE rental_payments SET status='confirmed', reject_reason=NULL, updated_at=? WHERE id=? AND status='pending'",
        )
        .run(now, paymentId);
      const payment = findPayment(database, paymentId);
      if (!payment) throw new RentalPaymentError("payment-not-found", "ไม่พบรายการชำระเงิน", 404);
      return payment;
    })
    .immediate();
}

/**
 * Refunds the whole charge for a booking the Controller refused. Only a still
 * pending payment is refunded: a confirmed lease is settled by time instead.
 */
export function rejectAndRefundRentalPayment(
  database: Database.Database,
  paymentId: string,
  reason: string,
  now = Date.now(),
) {
  ensureRentalSchema(database);
  const safeReason = clampText(reason, 200) || "เครื่องไม่พร้อมให้เช่า";
  return database
    .transaction(() => {
      const payment = findPayment(database, paymentId);
      if (!payment) throw new RentalPaymentError("payment-not-found", "ไม่พบรายการชำระเงิน", 404);
      if (payment.status !== "pending") return payment;
      const result = database
        .prepare(
          `UPDATE rental_payments
           SET status='rejected', reject_reason=?, refunded_satang=amount_satang,
               refunded_at=?, refund_note=?, settled_at=?, updated_at=?
           WHERE id=? AND status='pending' AND refunded_satang=0`,
        )
        .run(safeReason, now, safeReason, now, now, paymentId);
      if (result.changes === 1) credit(database, payment.userId, payment.amountSatang, now);
      return findPayment(database, paymentId)!;
    })
    .immediate();
}

export type ChargeExtensionInput = {
  userId: string;
  paymentId: string;
  hours: number;
  requestKey: string;
  now?: number;
};

export type ChargeExtensionResult = {
  payment: RentalPayment;
  balanceSatang: number;
  repeated: boolean;
  addedSeconds: number;
  addedSatang: number;
};

export function chargeRentalExtension(
  database: Database.Database,
  input: ChargeExtensionInput,
): ChargeExtensionResult {
  ensureRentalSchema(database);
  const userId = assertIdentifier(input.userId, "บัญชี");
  const hours = assertRentalHours(input.hours);
  const requestKey = assertRequestKey(input.requestKey);
  const now = input.now ?? Date.now();
  const addedSeconds = hours * RENTAL_HOUR_SECONDS;

  return database
    .transaction((): ChargeExtensionResult => {
      const payment = findPayment(database, input.paymentId);
      if (!payment || payment.userId !== userId)
        throw new RentalPaymentError("payment-not-found", "ไม่พบรายการชำระเงินสำหรับการเช่านี้", 404);
      if (payment.status !== "confirmed")
        throw new RentalPaymentError(
          "payment-not-ready",
          "รายการเช่านี้ยังไม่พร้อมให้ต่อเวลา",
          409,
        );
      const prior = database
        .prepare(
          "SELECT additional_seconds, amount_satang, status FROM rental_extension_payments WHERE payment_id=? AND request_key=?",
        )
        .get(payment.id, requestKey) as
        | { additional_seconds: number; amount_satang: number; status: "charged" | "released" }
        | undefined;
      if (prior) {
        if (
          prior.additional_seconds !== addedSeconds ||
          prior.status !== "charged"
        )
          throw new RentalPaymentError(
            "idempotency-conflict",
            "รหัสคำขอซ้ำกับรายการต่อเวลาอื่น กรุณาเริ่มรายการใหม่",
            409,
          );
        return {
          payment,
          balanceSatang: readBalance(database, userId),
          repeated: true,
          addedSeconds: prior.additional_seconds,
          addedSatang: prior.amount_satang,
        };
      }

      const addedSatang = assertRentalAmount(payment.hourlyPriceSatang * hours);
      database
        .prepare(
          "INSERT INTO rental_extension_payments(id,payment_id,request_key,additional_seconds,amount_satang,status,created_at) VALUES(?,?,?,?,?,'charged',?)",
        )
        .run(randomUUID(), payment.id, requestKey, addedSeconds, addedSatang, now);
      database
        .prepare(
          "UPDATE rental_payments SET extension_seconds=extension_seconds+?, extension_satang=extension_satang+?, updated_at=? WHERE id=?",
        )
        .run(addedSeconds, addedSatang, now, payment.id);
      const balanceSatang = debit(database, userId, addedSatang, now);
      const updated = findPayment(database, payment.id);
      if (!updated) throw new RentalPaymentError("payment-not-found", "ไม่พบรายการชำระเงิน", 404);
      return { payment: updated, balanceSatang, repeated: false, addedSeconds, addedSatang };
    })
    .immediate();
}

/** Refunds an extension charge when the Controller refuses the extension. */
export function releaseRentalExtension(
  database: Database.Database,
  input: { userId: string; paymentId: string; requestKey: string; now?: number },
) {
  ensureRentalSchema(database);
  const now = input.now ?? Date.now();
  return database
    .transaction(() => {
      const payment = findPayment(database, input.paymentId);
      if (!payment || payment.userId !== input.userId)
        throw new RentalPaymentError("payment-not-found", "ไม่พบรายการชำระเงินสำหรับการเช่านี้", 404);
      const extension = database
        .prepare(
          "SELECT id, additional_seconds, amount_satang, status FROM rental_extension_payments WHERE payment_id=? AND request_key=?",
        )
        .get(input.paymentId, input.requestKey) as
        | { id: string; additional_seconds: number; amount_satang: number; status: "charged" | "released" }
        | undefined;
      if (!extension || extension.status === "released") return payment;
      const released = database
        .prepare(
          "UPDATE rental_extension_payments SET status='released' WHERE id=? AND status='charged'",
        )
        .run(extension.id);
      if (released.changes === 1) {
        database
          .prepare(
            "UPDATE rental_payments SET extension_seconds=MAX(0,extension_seconds-?), extension_satang=MAX(0,extension_satang-?), updated_at=? WHERE id=?",
          )
          .run(extension.additional_seconds, extension.amount_satang, now, payment.id);
        credit(database, payment.userId, extension.amount_satang, now);
      }
      return findPayment(database, payment.id)!;
    })
    .immediate();
}

export type ControllerLeaseSnapshot = {
  /** The Controller-issued lease id. This is NOT the rental payment id. */
  id: string;
  /** The create idempotency key the web app sent, i.e. `rental_payments.id`. */
  paymentId?: string | null;
  state: string;
  startsAtUtc?: string | null;
  endedAtUtc?: string | null;
};

function formatUnusedDuration(seconds: number) {
  const totalSeconds = Math.max(0, Math.floor(seconds));
  const hours = Math.floor(totalSeconds / 3600);
  const minutes = Math.floor((totalSeconds % 3600) / 60);
  const remainderSeconds = totalSeconds % 60;
  const parts: string[] = [];
  if (hours > 0) parts.push(`${hours} ชม.`);
  if (minutes > 0) parts.push(`${minutes} นาที`);
  if (remainderSeconds > 0 || parts.length === 0) parts.push(`${remainderSeconds} วินาที`);
  return parts.join(" ");
}

/**
 * Prorates `paidSatang` by the unused fraction of the paid window and rounds
 * **half-up to the nearest satang** (0.5 satang rounds up, anything below rounds
 * down). The unused fraction is never rounded down to whole satang or baht:
 * e.g. `8000 × 2400 / 7200 = 2666.67` satang refunds 2667 satang.
 *
 * Integer arithmetic (BigInt) is used so no floating-point drift can move the
 * result across a half-satang boundary for large charges.
 */
function proratedRefundSatang(paidSatang: number, unusedMs: number, paidMs: number) {
  if (paidSatang <= 0 || unusedMs <= 0 || paidMs <= 0) return 0;
  const numerator = BigInt(paidSatang) * BigInt(unusedMs);
  const denominator = BigInt(paidMs);
  const two = BigInt(2);
  // floor((2n + d) / 2d) === round-half-up(n / d) for non-negative integers.
  return Number((numerator * two + denominator) / (denominator * two));
}

/**
 * Refund rules: the whole charge is refunded when the lease never became active
 * (no start instant, or it ended at/before its start), otherwise the unused
 * fraction of the total paid window — base duration plus charged extension
 * seconds/satang — is prorated and rounded half-up to satang.
 */
function calculateRefund(payment: RentalPayment, lease: ControllerLeaseSnapshot) {
  const paidSatang = payment.amountSatang + payment.extensionSatang;
  const paidSeconds = payment.durationSeconds + payment.extensionSeconds;
  if (paidSatang <= 0 || paidSeconds <= 0) return { refundSatang: 0, note: "" };

  const startedAt = lease.startsAtUtc ? Date.parse(lease.startsAtUtc) : Number.NaN;
  const endedAt = lease.endedAtUtc ? Date.parse(lease.endedAtUtc) : Number.NaN;

  // The Controller records starts_at only once the lease actually starts, so a
  // missing start instant (or an end at/before it) means nothing was consumed.
  if (!Number.isFinite(startedAt) || (Number.isFinite(endedAt) && endedAt <= startedAt))
    return { refundSatang: paidSatang, note: "คืนเต็มจำนวน เพราะเครื่องไม่พร้อมก่อนเริ่มเช่า" };
  if (!Number.isFinite(endedAt)) return { refundSatang: 0, note: "" };

  const paidMs = paidSeconds * 1000;
  const usedMs = Math.min(Math.max(endedAt - startedAt, 0), paidMs);
  const unusedMs = paidMs - usedMs;
  if (unusedMs <= 0) return { refundSatang: 0, note: "" };
  const refundSatang = proratedRefundSatang(paidSatang, unusedMs, paidMs);
  if (refundSatang <= 0) return { refundSatang: 0, note: "" };
  return {
    refundSatang,
    note: `คืนตามเวลาที่ไม่ได้ใช้ ${formatUnusedDuration(unusedMs / 1000)}`,
  };
}

export type SettleRentalsResult = { refundedSatang: number; refundedPayments: number };

/**
 * Settles unused time for ended leases. Safe to call on every read: a payment is
 * settled at most once (`settled_at IS NULL` guard) and the refund is capped by
 * the amount actually paid.
 */
export function settleRentalRefunds(
  database: Database.Database,
  leases: ControllerLeaseSnapshot[],
  options: { userId?: string; now?: number } = {},
): SettleRentalsResult {
  ensureRentalSchema(database);
  const now = options.now ?? Date.now();
  // A Controller lease id is random and is never the rental payment id: the
  // create idempotency key (exposed as paymentId) is. Fall back to the lease id
  // only for snapshots that predate the payment mapping.
  const leaseByPaymentId = new Map(
    leases.map((lease) => [lease.paymentId ?? lease.id, lease]),
  );
  if (leaseByPaymentId.size === 0) return { refundedSatang: 0, refundedPayments: 0 };

  const rows = database
    .prepare(
      `SELECT ${PAYMENT_COLUMNS} FROM rental_payments
       WHERE status='confirmed' AND settled_at IS NULL ${options.userId ? "AND user_id=?" : ""}`,
    )
    .all(...(options.userId ? [options.userId] : [])) as PaymentRow[];

  let refundedSatang = 0;
  let refundedPayments = 0;
  for (const row of rows) {
    const payment = toPayment(row);
    const lease = leaseByPaymentId.get(payment.id);
    if (!lease) continue;
    const ended =
      Boolean(lease.endedAtUtc) ||
      lease.state === "failed" ||
      lease.state === "completed";
    if (!ended) continue;
    // A terminal lease can briefly lack its end instant (cancel raced the
    // ended_at write, or the row predates the migration). Proration needs that
    // instant: leave the payment unsettled and settle it on a later read once
    // ended_at exists. A lease that never started keeps the full-refund path.
    if (!lease.endedAtUtc && lease.startsAtUtc) continue;
    const { refundSatang, note } = calculateRefund(payment, lease);
    let applied = 0;
    try {
      applied = database
        .transaction(() => {
          const current = findPayment(database, payment.id);
          if (!current || current.settledAt !== null) return 0;
          const paid = current.amountSatang + current.extensionSatang;
          const refundable = Math.min(refundSatang, paid - current.refundedSatang);
          const safeRefund = Math.max(0, refundable);
          const refundedAt = safeRefund > 0 ? now : null;
          const refundNote = safeRefund > 0 ? note || null : null;
          const result = database
            .prepare(
              `UPDATE rental_payments
               SET refunded_satang=refunded_satang+?, refunded_at=?, refund_note=?, settled_at=?, updated_at=?
               WHERE id=? AND settled_at IS NULL`,
            )
            .run(safeRefund, refundedAt, refundNote, now, now, current.id);
          if (result.changes !== 1) return 0;
          if (safeRefund > 0) credit(database, current.userId, safeRefund, now);
          return safeRefund;
        })
        .immediate();
    } catch {
      // A missing/deleted account must not block the rest of the history read.
      continue;
    }
    if (applied > 0) {
      refundedSatang += applied;
      refundedPayments += 1;
    }
  }
  return { refundedSatang, refundedPayments };
}

export type PendingReconcileOutcome =
  | { kind: "created" }
  | { kind: "rejected"; reason: string }
  | { kind: "unknown" };

/**
 * Retries Controller creation for payments whose outcome was unknown (the
 * Controller was unreachable). The Controller returns the existing lease for a
 * repeated idempotency key, so a retry is safe.
 */
export async function reconcilePendingRentals(
  database: Database.Database,
  userId: string,
  createLease: (payment: RentalPayment) => Promise<PendingReconcileOutcome>,
  now = Date.now(),
) {
  ensureRentalSchema(database);
  const rows = database
    .prepare(
      `SELECT ${PAYMENT_COLUMNS} FROM rental_payments
       WHERE user_id=? AND status='pending' ORDER BY created_at, rowid LIMIT 3`,
    )
    .all(userId) as PaymentRow[];
  const settled: RentalPayment[] = [];
  for (const row of rows) {
    const payment = toPayment(row);
    let outcome: PendingReconcileOutcome;
    try {
      outcome = await createLease(payment);
    } catch {
      outcome = { kind: "unknown" };
    }
    if (outcome.kind === "created") settled.push(confirmRentalPayment(database, payment.id, now));
    else if (outcome.kind === "rejected")
      settled.push(rejectAndRefundRentalPayment(database, payment.id, outcome.reason, now));
  }
  return settled;
}

export function getMemberBalance(database: Database.Database, userId: string) {
  ensureRentalSchema(database);
  return readBalance(database, userId);
}

export function listRentalPayments(database: Database.Database, userId?: string) {
  ensureRentalSchema(database);
  const rows = database
    .prepare(
      `SELECT ${PAYMENT_COLUMNS} FROM rental_payments
       ${userId ? "WHERE user_id=?" : ""} ORDER BY created_at DESC, rowid DESC`,
    )
    .all(...(userId ? [userId] : [])) as PaymentRow[];
  return rows.map(toPayment);
}

export function listRentalPaymentsByIds(database: Database.Database, paymentIds: string[]) {
  ensureRentalSchema(database);
  if (paymentIds.length === 0) return [] as RentalPayment[];
  const unique = [...new Set(paymentIds)].slice(0, 500);
  const placeholders = unique.map(() => "?").join(",");
  const rows = database
    .prepare(`SELECT ${PAYMENT_COLUMNS} FROM rental_payments WHERE id IN (${placeholders})`)
    .all(...unique) as PaymentRow[];
  return rows.map(toPayment);
}

export function rentalPaymentSummary(database: Database.Database, userId?: string) {
  ensureRentalSchema(database);
  const row = database
    .prepare(
      `SELECT
         COALESCE(SUM(CASE WHEN status='confirmed' THEN 1 ELSE 0 END),0) AS totalRentals,
         COALESCE(SUM(CASE WHEN status='confirmed' THEN amount_satang + extension_satang - refunded_satang ELSE 0 END),0) AS revenueSatang,
         COALESCE(SUM(CASE WHEN status='confirmed' THEN refunded_satang ELSE 0 END),0) AS refundedSatang
       FROM rental_payments ${userId ? "WHERE user_id=?" : ""}`,
    )
    .get(...(userId ? [userId] : [])) as {
    totalRentals: number;
    revenueSatang: number;
    refundedSatang: number;
  };
  return row;
}
