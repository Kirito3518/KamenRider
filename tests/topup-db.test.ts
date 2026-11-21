// @vitest-environment node
import { afterEach, beforeEach, describe, expect, it } from "vitest";
import Database from "better-sqlite3";
import { createProcessingToken } from "@/lib/topup/reference";
import { ensureTopupSchema } from "@/lib/topup/schema";
import { TopupError } from "@/lib/topup/errors";
import {
  beginSlipProcessing,
  createTopup,
  expireStaleTopups,
  markTopupFailed,
  markTopupProcessingUnknown,
  requireTopupRow,
} from "@/lib/topup/transactions";
import { manualReviewTopup } from "@/lib/topup/services/review-service";
import { creditSlipTopup } from "@/lib/topup/wallet";
import {
  archiveBankAccount,
  createBankAccount,
  getAdminTopupSettings,
  getProviderCredentials,
  getPublicTopupConfig,
  updateTopupProvider,
  updateTopupSettings,
} from "@/lib/topup/settings";

const USER_ID = "user-topup-1";
const ACTOR = { id: "admin-1", name: "Admin Test", email: "admin@example.com" };

function makeDb() {
  const db = new Database(":memory:");
  db.exec(`
    CREATE TABLE user(
      id TEXT PRIMARY KEY,
      name TEXT,
      email TEXT,
      balanceSatang INTEGER NOT NULL DEFAULT 0,
      updatedAt TEXT
    );
  `);
  db.prepare(
    "INSERT INTO user(id,name,email,balanceSatang,updatedAt) VALUES(?,?,?,?,?)",
  ).run(USER_ID, "Topup User", "topup@example.com", 100_000, new Date(0).toISOString());
  ensureTopupSchema(db);
  return db;
}

function createSlipTopup(
  db: Database.Database,
  options: {
    amount?: number;
    key?: string;
    channel?: "promptpay" | "bank_transfer";
    hash?: string;
  } = {},
) {
  const now = Date.now();
  return createTopup(db, {
    userId: USER_ID,
    userName: "Topup User",
    userEmail: "topup@example.com",
    channel: options.channel ?? "promptpay",
    requestedSatang: options.amount ?? 10_000,
    idempotencyKey: options.key ?? crypto.randomUUID(),
    fingerprint: `test:${options.amount ?? 10_000}:${options.hash ?? "default"}:${options.channel ?? "promptpay"}`,
    expiresAt: now + 60_000,
    promptpay:
      (options.channel ?? "promptpay") === "promptpay"
        ? { type: "msisdn", number: "0812345678", accountName: "ร้านทดสอบ" }
        : undefined,
    bank:
      options.channel === "bank_transfer"
        ? {
            id: "bank-1",
            code: "004",
            name: "ธนาคารกสิกรไทย",
            accountNumber: "1234567890",
            accountName: "ร้านทดสอบ",
          }
        : undefined,
    now,
  }).row;
}

function processAndCredit(
  db: Database.Database,
  row: ReturnType<typeof createSlipTopup>,
  transRef = `REF-${crypto.randomUUID()}`,
) {
  const token = createProcessingToken();
  const processing = beginSlipProcessing(db, {
    topupId: row.id,
    token,
    path: `${row.id}.png`,
    mime: "image/png",
    hash: crypto.randomUUID().replace(/-/g, ""),
  });
  return creditSlipTopup(db, {
    topupId: processing.id,
    processingToken: token,
    provider: "easyslip",
    transRef,
    amountSatang: processing.requested_satang ?? 0,
    verifiedRecovered: false,
    providerRequestId: null,
    slip: {
      senderName: "ผู้โอน",
      receiverName: "ร้านทดสอบ",
      receiverAccount: "0812345678",
      bankCode: "004",
      dateIso: new Date().toISOString(),
      code: "verified",
      message: "ตรวจสอบสลิปผ่าน",
    },
  });
}

function balance(db: Database.Database) {
  return (
    db.prepare("SELECT balanceSatang FROM user WHERE id = ?").get(USER_ID) as {
      balanceSatang: number;
    }
  ).balanceSatang;
}

function createReviewableTopup(
  db: Database.Database,
  receiver: {
    receiverName: string;
    receiverBankCode: string;
    receiverAccountNumber: string;
  },
) {
  const now = Date.now();
  const row = createTopup(db, {
    userId: USER_ID,
    userName: "Topup User",
    userEmail: "topup@example.com",
    channel: "promptpay",
    requestedSatang: 10_000,
    idempotencyKey: crypto.randomUUID(),
    fingerprint: `review:${crypto.randomUUID()}`,
    expiresAt: now + 60_000,
    promptpay: {
      type: "msisdn",
      number: "0812345678",
      accountName: "ร้านทดสอบ",
      bankCode: "004",
      bankName: "ธนาคารกสิกรไทย",
      accountNumber: "1234567890",
    },
    now,
  }).row;
  const token = createProcessingToken();
  beginSlipProcessing(db, {
    topupId: row.id,
    token,
    path: `${row.id}.png`,
    mime: "image/png",
    hash: crypto.randomUUID().replace(/-/g, ""),
  });
  return markTopupProcessingUnknown(db, {
    topupId: row.id,
    code: "recipient_unverified",
    message: "รอผู้ดูแลตรวจสอบ",
    slip: {
      provider: "easyslip",
      transRef: `REF-${crypto.randomUUID()}`,
      amountSatang: row.requested_satang,
      dateIso: new Date().toISOString(),
      senderName: "ผู้โอน",
      receiverName: receiver.receiverName,
      receiverBankCode: receiver.receiverBankCode,
      receiverBankName: null,
      receiverAccountNumber: receiver.receiverAccountNumber,
      receiverProxyType: null,
      receiverProxyValue: null,
    },
  });
}

describe("top-up schema and wallet credit", () => {
  let db: Database.Database;

  beforeEach(() => {
    db = makeDb();
  });

  afterEach(() => {
    db.close();
  });

  it("creates the schema idempotently with disabled default channels", () => {
    const tables = db
      .prepare("SELECT name FROM sqlite_master WHERE type='table' ORDER BY name")
      .all() as { name: string }[];
    const names = tables.map((table) => table.name);
    expect(names).toContain("topup_transactions");
    expect(names).toContain("wallet_transactions");
    expect(names).toContain("topup_settings");
    expect(names).toContain("topup_provider_settings");
    ensureTopupSchema(db);
    const settings = getAdminTopupSettings(db);
    expect(settings.settings.truemoney.enabled).toBe(false);
    expect(settings.settings.promptpay.enabled).toBe(false);
    expect(settings.settings.bankTransfer.enabled).toBe(false);
    expect(settings.settings.coupon.enabled).toBe(false);
  });

  it("is idempotent for the same request id and rejects a different fingerprint", () => {
    const key = crypto.randomUUID();
    const first = createTopup(db, {
      userId: USER_ID,
      userName: "Topup User",
      userEmail: "topup@example.com",
      channel: "promptpay",
      requestedSatang: 10_000,
      idempotencyKey: key,
      fingerprint: "same",
      expiresAt: Date.now() + 60_000,
    });
    const second = createTopup(db, {
      userId: USER_ID,
      userName: "Topup User",
      userEmail: "topup@example.com",
      channel: "promptpay",
      requestedSatang: 10_000,
      idempotencyKey: key,
      fingerprint: "same",
      expiresAt: Date.now() + 60_000,
    });
    expect(second.repeated).toBe(true);
    expect(second.row.id).toBe(first.row.id);
    expect(() =>
      createTopup(db, {
        userId: USER_ID,
        userName: "Topup User",
        userEmail: "topup@example.com",
        channel: "promptpay",
        requestedSatang: 20_000,
        idempotencyKey: key,
        fingerprint: "different",
        expiresAt: Date.now() + 60_000,
      }),
    ).toThrow(TopupError);
  });

  it("credits the wallet exactly once and records one ledger row", () => {
    const row = createSlipTopup(db);
    const first = processAndCredit(db, row);
    expect(first.repeated).toBe(false);
    expect(balance(db)).toBe(110_000);
    expect(first.balanceSatang).toBe(110_000);

    const repeated = creditSlipTopup(db, {
      topupId: row.id,
      processingToken: "stale-token",
      provider: "easyslip",
      transRef: "REF-REPEAT",
      amountSatang: 10_000,
      verifiedRecovered: false,
      providerRequestId: null,
      slip: {
        senderName: null,
        receiverName: null,
        receiverAccount: null,
        bankCode: null,
        dateIso: null,
        code: "verified",
        message: "repeat",
      },
    });
    expect(repeated.repeated).toBe(true);
    expect(balance(db)).toBe(110_000);
    expect(
      (
        db
          .prepare("SELECT count(*) AS count FROM wallet_transactions")
          .get() as { count: number }
      ).count,
    ).toBe(1);
  });

  it("rejects duplicate slip reference across transactions without crediting again", () => {
    const first = createSlipTopup(db, { hash: "hash-a" });
    processAndCredit(db, first, "SAME-TRANS-REF");
    const second = createSlipTopup(db, { hash: "hash-b" });
    const token = createProcessingToken();
    beginSlipProcessing(db, {
      topupId: second.id,
      token,
      path: "second.png",
      mime: "image/png",
      hash: "hash-b-2",
    });
    expect(() =>
      creditSlipTopup(db, {
        topupId: second.id,
        processingToken: token,
        provider: "easyslip",
        transRef: "SAME-TRANS-REF",
        amountSatang: 10_000,
        verifiedRecovered: false,
        providerRequestId: null,
        slip: {
          senderName: null,
          receiverName: null,
          receiverAccount: null,
          bankCode: null,
          dateIso: null,
          code: "verified",
          message: "duplicate",
        },
      }),
    ).toThrow(/สลิปนี้ถูกใช้/);
    expect(balance(db)).toBe(110_000);
  });

  it("never credits failed or expired transactions", () => {
    const failed = createSlipTopup(db, { hash: "fail-hash" });
    markTopupFailed(db, {
      topupId: failed.id,
      verificationStatus: "rejected",
      verificationCode: "amount_mismatch",
      verificationMessage: "ยอดไม่ตรง",
      failureCode: "amount_mismatch",
      failureMessage: "ยอดไม่ตรง",
    });
    expect(() => processAndCredit(db, failed)).toThrow();
    expect(balance(db)).toBe(100_000);

    const expired = createTopup(db, {
      userId: USER_ID,
      userName: "Topup User",
      userEmail: "topup@example.com",
      channel: "promptpay",
      requestedSatang: 5_000,
      idempotencyKey: crypto.randomUUID(),
      fingerprint: "expired",
      expiresAt: Date.now() - 1_000,
    }).row;
    expireStaleTopups(db);
    expect(requireTopupRow(db, expired.id).status).toBe("expired");
    expect(() => processAndCredit(db, expired)).toThrow();
    expect(balance(db)).toBe(100_000);
  });

  it("enforces provider/channel prerequisites and never returns raw API keys", () => {
    const initial = getAdminTopupSettings(db);
    expect(() =>
      updateTopupSettings(db, ACTOR, {
        expectedUpdatedAt: initial.settings.updatedAt,
        promptpay: {
          enabled: true,
          type: "msisdn",
          number: "0812345678",
          accountName: "ร้านทดสอบ",
          bankCode: "004",
          accountNumber: "1234567890",
        },
      }),
    ).toThrow(TopupError);

    const easySlipKey = "123e4567-e89b-42d3-a456-426614174000";
    const providerUpdate = updateTopupProvider(db, ACTOR, "easyslip", {
      expectedUpdatedAt: initial.providers.easyslip.updatedAt,
      apiKey: easySlipKey,
    });
    expect(providerUpdate.providers.easyslip.configured).toBe(true);
    expect(JSON.stringify(providerUpdate)).not.toContain(easySlipKey);
    expect(
      (
        db
          .prepare("SELECT api_key_enc FROM topup_provider_settings WHERE provider='easyslip'")
          .get() as { api_key_enc: string }
      ).api_key_enc,
    ).toMatch(/^enc:v1:/);
    expect(getProviderCredentials(db, "easyslip")?.apiKey).toBe(easySlipKey);

    const afterKey = getAdminTopupSettings(db);
    expect(() =>
      updateTopupSettings(db, ACTOR, {
        expectedUpdatedAt: afterKey.settings.updatedAt,
        promptpay: {
          enabled: true,
          type: "msisdn",
          number: "0812345678",
          accountName: "ร้านทดสอบ",
        },
      }),
    ).toThrow(TopupError);
    const enabled = updateTopupSettings(db, ACTOR, {
      expectedUpdatedAt: afterKey.settings.updatedAt,
      promptpay: {
        enabled: true,
        type: "msisdn",
        number: "0812345678",
        accountName: "ร้านทดสอบ",
        bankCode: "004",
        accountNumber: "1234567890",
      },
    });
    expect(enabled.settings.promptpay.enabled).toBe(true);
    const publicConfig = getPublicTopupConfig(db);
    expect(publicConfig.promptpay.enabled).toBe(true);
    expect(publicConfig.promptpay.numberMasked).not.toBe("0812345678");
    expect(JSON.stringify(publicConfig)).not.toContain(easySlipKey);
    expect(JSON.stringify(publicConfig)).not.toContain("0812345678");
  });

  it("manages bank accounts and protects the last active account", () => {
    const initial = getAdminTopupSettings(db);
    updateTopupProvider(db, ACTOR, "easyslip", {
      expectedUpdatedAt: initial.providers.easyslip.updatedAt,
      apiKey: "123e4567-e89b-42d3-a456-426614174000",
    });
    const account = createBankAccount(db, ACTOR, {
      bankCode: "004",
      bankName: "ธนาคารกสิกรไทย",
      accountNumber: "1234567890",
      accountName: "ร้านทดสอบ",
      logoKey: "kbank",
      isActive: true,
    });
    expect(account.bankCode).toBe("004");
    expect(account.logoKey).toBe("kbank");

    const afterCreate = getAdminTopupSettings(db);
    updateTopupSettings(db, ACTOR, {
      expectedUpdatedAt: afterCreate.settings.updatedAt,
      bankTransfer: { enabled: true },
    });
    expect(() => archiveBankAccount(db, ACTOR, account.id)).toThrow(/บัญชีที่เปิดใช้งาน/);

    const second = createBankAccount(db, ACTOR, {
      bankCode: "014",
      bankName: "ธนาคารไทยพาณิชย์",
      accountNumber: "9876543210",
      accountName: "ร้านทดสอบ",
      logoKey: "scb",
      isActive: true,
    });
    const archived = archiveBankAccount(db, ACTOR, account.id);
    expect(archived.archivedAt).not.toBeNull();
    expect(getPublicTopupConfig(db).bankTransfer.accounts.map((item) => item.id)).toEqual([
      second.id,
    ]);
  });

  it("lets an Admin approve a reviewed slip only when the selected account matches", () => {
    const db = makeDb();
    try {
      const row = createReviewableTopup(db, {
        receiverName: "ร้านทดสอบ",
        receiverBankCode: "004",
        receiverAccountNumber: "1234567890",
      });
      expect(requireTopupRow(db, row.id).status).toBe("processing");
      const result = manualReviewTopup(db, ACTOR, row.id, { action: "approve" });
      expect(result.outcome).toBe("completed");
      expect(balance(db)).toBe(110_000);
      const saved = requireTopupRow(db, row.id);
      expect(saved.status).toBe("completed");
      expect(saved.verification_code).toBe("manual_approved");
    } finally {
      db.close();
    }
  });

  it("auto-rejects an Admin approval when the slip receiver does not match", () => {
    const db = makeDb();
    try {
      const row = createReviewableTopup(db, {
        receiverName: "ร้านอื่น",
        receiverBankCode: "014",
        receiverAccountNumber: "1111111111",
      });
      const result = manualReviewTopup(db, ACTOR, row.id, { action: "approve" });
      expect(result.outcome).toBe("failed");
      expect(balance(db)).toBe(100_000);
      const saved = requireTopupRow(db, row.id);
      expect(saved.status).toBe("failed");
      expect(saved.failure_code).toBe("recipient_mismatch");
    } finally {
      db.close();
    }
  });

  it("allows an Admin to reject a reviewed slip without crediting", () => {
    const db = makeDb();
    try {
      const row = createReviewableTopup(db, {
        receiverName: "ร้านทดสอบ",
        receiverBankCode: "004",
        receiverAccountNumber: "1234567890",
      });
      const result = manualReviewTopup(db, ACTOR, row.id, {
        action: "reject",
        reason: "ตรวจรูปสลิปแล้วไม่ตรง",
      });
      expect(result.outcome).toBe("failed");
      expect(result.message).toBe("ตรวจรูปสลิปแล้วไม่ตรง");
      expect(balance(db)).toBe(100_000);
      expect(requireTopupRow(db, row.id).failure_code).toBe("manual_rejected");
    } finally {
      db.close();
    }
  });

  it("does not review a row that is not awaiting review", () => {
    const db = makeDb();
    try {
      const row = createSlipTopup(db);
      expect(() =>
        manualReviewTopup(db, ACTOR, row.id, { action: "approve" }),
      ).toThrow(TopupError);
    } finally {
      db.close();
    }
  });
});
