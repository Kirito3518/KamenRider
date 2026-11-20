/**
 * Top-up settings, provider credentials, and bank-account administration.
 *
 * Secrets are stored encrypted in `topup_provider_settings` and are never part
 * of any response returned to the browser. The public config contains only the
 * values a payer needs (limits, opening channels, receiver display info).
 */

import type Database from "better-sqlite3";
import { randomUUID } from "node:crypto";
import { z } from "zod";
import {
  BANK_TRANSFER_EXPIRY_MS,
  MAX_BANK_LOGO_BYTES,
  PROMPTPAY_QR_EXPIRY_MS,
  TOPUP_MAX_SATANG,
  TOPUP_MIN_SATANG,
  isPromptpayType,
  isSlipProvider,
  type PromptpayType,
  type SlipProvider,
} from "@/lib/topup/constants";
import {
  isValidBankAccountNumber,
  isValidNatId,
  isValidPhoneNumber,
  maskPromptpayNumber,
  maskSecret,
} from "@/lib/topup/amount";
import { isKnownBankLogoKey } from "@/lib/topup/banks";
import { TopupError } from "@/lib/topup/errors";
import { ensureTopupSchema } from "@/lib/topup/schema";
import { decryptSecret, encryptSecret } from "@/lib/topup/secrets";
import type {
  AdminTopupSettingsResponse,
  BankAccountAdmin,
  ProviderAdminStatus,
  ProviderTestResult,
  TopupPublicConfig,
  TopupSettingsAdmin,
  TopupSettingsAuditEntry,
} from "@/lib/topup/types";
import { easySlipTestConnection } from "@/lib/topup/providers/easyslip";
import { slipOkTestConnection } from "@/lib/topup/providers/slipok";

export type SettingsRow = {
  id: number;
  truemoney_enabled: number;
  truemoney_phone: string | null;
  promptpay_enabled: number;
  promptpay_type: PromptpayType;
  promptpay_number: string | null;
  promptpay_account_name: string | null;
  bank_transfer_enabled: number;
  coupon_enabled: number;
  coupon_message: string;
  slip_provider: SlipProvider;
  updated_at: number;
  updated_by: string | null;
};

export type ProviderRow = {
  provider: SlipProvider;
  api_key_enc: string | null;
  branch_id: string | null;
  last_test_at: number | null;
  last_test_ok: number | null;
  last_test_message: string | null;
  updated_at: number;
  updated_by: string | null;
};

export type BankAccountRow = {
  id: string;
  bank_code: string;
  bank_name: string;
  account_number: string;
  account_name: string;
  logo_key: string | null;
  logo_data_url: string | null;
  is_active: number;
  archived_at: number | null;
  created_at: number;
  updated_at: number;
};

export type Actor = { id: string; name: string };

const DEFAULT_COUPON_MESSAGE = "คูปองยังไม่เปิดให้บริการในขณะนี้";

function normalizeDigits(value: string) {
  return value.replace(/[\s-]/g, "");
}

function nowMs() {
  return Date.now();
}

function readSettingsRow(database: Database.Database): SettingsRow {
  ensureTopupSchema(database);
  const row = database
    .prepare("SELECT * FROM topup_settings WHERE id = 1")
    .get() as SettingsRow | undefined;
  if (!row) throw new TopupError("internal-error", "ไม่พบการตั้งค่าระบบเติมเงิน", 500);
  return row;
}

function readProviderRows(database: Database.Database): ProviderRow[] {
  ensureTopupSchema(database);
  return database
    .prepare("SELECT * FROM topup_provider_settings ORDER BY provider")
    .all() as ProviderRow[];
}

function readProviderRow(database: Database.Database, provider: SlipProvider) {
  const row = database
    .prepare("SELECT * FROM topup_provider_settings WHERE provider = ?")
    .get(provider) as ProviderRow | undefined;
  if (!row) throw new TopupError("internal-error", "ไม่พบการตั้งค่าผู้ให้บริการ", 500);
  return row;
}

function decryptedApiKey(row: ProviderRow): string | null {
  return decryptSecret(row.api_key_enc);
}

function providerConfigured(row: ProviderRow): boolean {
  const key = decryptedApiKey(row);
  if (!key) return false;
  if (row.provider === "slipok" && !row.branch_id) return false;
  return true;
}

function providerStatus(row: ProviderRow): ProviderAdminStatus {
  const key = decryptedApiKey(row);
  return {
    provider: row.provider,
    configured: providerConfigured(row),
    maskedKey: key ? maskSecret(key) : "",
    branchId: row.branch_id,
    lastTestAt: row.last_test_at,
    lastTestOk:
      row.last_test_ok === null ? null : row.last_test_ok === 1,
    lastTestMessage: row.last_test_message,
    updatedAt: row.updated_at,
  };
}

function userName(database: Database.Database, userId: string | null): string | null {
  if (!userId) return null;
  const row = database
    .prepare("SELECT name FROM user WHERE id = ?")
    .get(userId) as { name: string } | undefined;
  return row?.name ?? null;
}

function settingsForAdmin(
  database: Database.Database,
  row: SettingsRow,
): TopupSettingsAdmin {
  return {
    truemoney: {
      enabled: row.truemoney_enabled === 1,
      phone: row.truemoney_phone,
    },
    promptpay: {
      enabled: row.promptpay_enabled === 1,
      type: row.promptpay_type,
      number: row.promptpay_number,
      accountName: row.promptpay_account_name,
    },
    bankTransfer: { enabled: row.bank_transfer_enabled === 1 },
    coupon: {
      enabled: row.coupon_enabled === 1,
      message: row.coupon_message,
    },
    slipProvider: row.slip_provider,
    updatedAt: row.updated_at,
    updatedBy: row.updated_by,
    updatedByName: userName(database, row.updated_by),
  };
}

function bankAccountFromRow(row: BankAccountRow): BankAccountAdmin {
  return {
    id: row.id,
    bankCode: row.bank_code,
    bankName: row.bank_name,
    accountNumber: row.account_number,
    accountName: row.account_name,
    logoKey: row.logo_key,
    logoDataUrl: row.logo_data_url,
    isActive: row.is_active === 1,
    archivedAt: row.archived_at,
    createdAt: row.created_at,
    updatedAt: row.updated_at,
  };
}

export function getTopupSettingsRow(database: Database.Database) {
  return readSettingsRow(database);
}

export function getSelectedSlipProvider(database: Database.Database) {
  return readSettingsRow(database).slip_provider;
}

/** Server-only credential accessor. Never expose its return value to clients. */
export function getProviderCredentials(
  database: Database.Database,
  provider: SlipProvider,
): { apiKey: string; branchId: string | null } | null {
  const row = readProviderRow(database, provider);
  const apiKey = decryptedApiKey(row);
  if (!apiKey) return null;
  if (provider === "slipok" && !row.branch_id) return null;
  return { apiKey, branchId: row.branch_id };
}

export function countActiveBankAccounts(database: Database.Database) {
  ensureTopupSchema(database);
  return (
    database
      .prepare(
        "SELECT count(*) AS count FROM topup_bank_accounts WHERE archived_at IS NULL AND is_active = 1",
      )
      .get() as { count: number }
  ).count;
}

export function countPendingSlipChannelTopups(database: Database.Database) {
  ensureTopupSchema(database);
  return (
    database
      .prepare(
        `SELECT count(*) AS count FROM topup_transactions
         WHERE channel IN ('promptpay','bank_transfer')
           AND status IN ('pending','processing')`,
      )
      .get() as { count: number }
  ).count;
}

export function listBankAccounts(
  database: Database.Database,
  includeArchived = false,
) {
  ensureTopupSchema(database);
  const rows = database
    .prepare(
      `SELECT * FROM topup_bank_accounts
       ${includeArchived ? "" : "WHERE archived_at IS NULL"}
       ORDER BY archived_at IS NOT NULL, is_active DESC, created_at DESC`,
    )
    .all() as BankAccountRow[];
  return rows.map(bankAccountFromRow);
}

export function getActiveBankAccount(
  database: Database.Database,
  accountId: string,
): BankAccountRow | null {
  ensureTopupSchema(database);
  const row = database
    .prepare(
      "SELECT * FROM topup_bank_accounts WHERE id = ? AND archived_at IS NULL AND is_active = 1",
    )
    .get(accountId) as BankAccountRow | undefined;
  return row ?? null;
}

export function getBankAccountRow(
  database: Database.Database,
  accountId: string,
): BankAccountRow | null {
  ensureTopupSchema(database);
  const row = database
    .prepare("SELECT * FROM topup_bank_accounts WHERE id = ?")
    .get(accountId) as BankAccountRow | undefined;
  return row ?? null;
}

export function getPublicTopupConfig(database: Database.Database): TopupPublicConfig {
  const row = readSettingsRow(database);
  const providers = readProviderRows(database);
  const selected = providers.find((provider) => provider.provider === row.slip_provider);
  const slipVerificationAvailable = selected ? providerConfigured(selected) : false;
  const accounts = listBankAccounts(database).map((account) => ({
    id: account.id,
    bankCode: account.bankCode,
    bankName: account.bankName,
    accountNumber: account.accountNumber,
    accountName: account.accountName,
    logoKey: account.logoKey,
    logoDataUrl: account.logoDataUrl,
  }));
  const bankTransferEnabled = row.bank_transfer_enabled === 1;
  const promptpayEnabled = row.promptpay_enabled === 1;
  return {
    minSatang: TOPUP_MIN_SATANG,
    maxSatang: TOPUP_MAX_SATANG,
    qrExpirySeconds: Math.round(PROMPTPAY_QR_EXPIRY_MS / 1000),
    bankExpirySeconds: Math.round(BANK_TRANSFER_EXPIRY_MS / 1000),
    slipVerificationAvailable,
    bankRail: promptpayEnabled
      ? "promptpay_qr"
      : bankTransferEnabled
        ? "bank_transfer"
        : null,
    truemoney: { enabled: row.truemoney_enabled === 1 },
    promptpay: {
      enabled: promptpayEnabled,
      type: row.promptpay_number ? row.promptpay_type : null,
      numberMasked: maskPromptpayNumber(
        row.promptpay_type,
        row.promptpay_number,
      ),
      accountName: row.promptpay_account_name,
    },
    bankTransfer: {
      enabled: bankTransferEnabled,
      accounts,
    },
    coupon: {
      enabled: row.coupon_enabled === 1,
      message: row.coupon_message || DEFAULT_COUPON_MESSAGE,
    },
  };
}

export function getAdminTopupSettings(
  database: Database.Database,
): AdminTopupSettingsResponse {
  const row = readSettingsRow(database);
  const providers = readProviderRows(database);
  const byProvider = new Map(providers.map((provider) => [provider.provider, provider]));
  const easyslipRow = byProvider.get("easyslip");
  const slipokRow = byProvider.get("slipok");
  if (!easyslipRow || !slipokRow) {
    throw new TopupError("internal-error", "ข้อมูลผู้ให้บริการไม่ครบถ้วน", 500);
  }
  const auditRows = database
    .prepare(
      `SELECT id, actor_name, action, target_type, target_id, changes_json, created_at
       FROM topup_settings_audit ORDER BY created_at DESC, rowid DESC LIMIT 10`,
    )
    .all() as {
    id: string;
    actor_name: string | null;
    action: string;
    target_type: string;
    target_id: string | null;
    changes_json: string;
    created_at: number;
  }[];
  const audit: TopupSettingsAuditEntry[] = auditRows.map((entry) => ({
    id: entry.id,
    actorName: entry.actor_name || "ผู้ดูแลระบบ",
    action: entry.action,
    targetType: entry.target_type,
    targetId: entry.target_id,
    changes: entry.changes_json,
    createdAt: entry.created_at,
  }));
  const pendingVerifications = (
    database
      .prepare(
        `SELECT count(*) AS count FROM topup_transactions
         WHERE status IN ('pending','processing')
           AND (status = 'processing' OR slip_image_hash IS NOT NULL)`,
      )
      .get() as { count: number }
  ).count;
  return {
    settings: settingsForAdmin(database, row),
    providers: {
      easyslip: providerStatus(easyslipRow),
      slipok: providerStatus(slipokRow),
    },
    bankAccounts: listBankAccounts(database, true),
    audit,
    pendingVerifications,
  };
}

export function recordTopupAudit(
  database: Database.Database,
  actor: Actor | null,
  action: string,
  targetType: string,
  targetId: string | null,
  changes: Record<string, unknown>,
) {
  ensureTopupSchema(database);
  const text = JSON.stringify(changes).slice(0, 4_000);
  database
    .prepare(
      `INSERT INTO topup_settings_audit
       (id, actor_id, actor_name, action, target_type, target_id, changes_json, created_at)
       VALUES (?,?,?,?,?,?,?,?)`,
    )
    .run(
      randomUUID(),
      actor?.id ?? null,
      actor?.name?.slice(0, 120) ?? null,
      action.slice(0, 80),
      targetType.slice(0, 40),
      targetId?.slice(0, 120) ?? null,
      text,
      nowMs(),
    );
}

const settingsUpdateSchema = z
  .object({
    expectedUpdatedAt: z.number().int().nonnegative(),
    truemoney: z
      .object({
        enabled: z.boolean(),
        phone: z.string().max(20).nullable().optional(),
      })
      .strict()
      .optional(),
    promptpay: z
      .object({
        enabled: z.boolean(),
        type: z.enum(["msisdn", "natId"]).optional(),
        number: z.string().max(20).nullable().optional(),
        accountName: z.string().max(120).nullable().optional(),
      })
      .strict()
      .optional(),
    bankTransfer: z.object({ enabled: z.boolean() }).strict().optional(),
    coupon: z
      .object({
        enabled: z.boolean(),
        message: z.string().max(200).optional(),
      })
      .strict()
      .optional(),
    slipProvider: z.enum(["easyslip", "slipok"]).optional(),
  })
  .strict();

export type SettingsUpdateInput = z.infer<typeof settingsUpdateSchema>;

function validatePromptpay(
  type: PromptpayType,
  number: string,
): string {
  const digits = normalizeDigits(number);
  if (type === "msisdn" && !isValidPhoneNumber(digits)) {
    throw new TopupError(
      "invalid-request",
      "หมายเลข PromptPay ต้องเป็นเบอร์โทรศัพท์ 10 หลักขึ้นต้นด้วย 0",
      400,
    );
  }
  if (type === "natId" && !isValidNatId(digits)) {
    throw new TopupError(
      "invalid-request",
      "หมายเลข PromptPay ต้องเป็นเลขบัตรประชาชน 13 หลัก",
      400,
    );
  }
  return digits;
}

function ensureProviderReadyForSlip(database: Database.Database, provider: SlipProvider) {
  const row = readProviderRow(database, provider);
  if (!providerConfigured(row)) {
    throw new TopupError(
      "not-configured",
      "ยังไม่ได้ตั้งค่า API Key ของผู้ให้บริการตรวจสลิปที่เลือก",
      409,
    );
  }
}

function ensureEasySlipReady(database: Database.Database) {
  const row = readProviderRow(database, "easyslip");
  if (!decryptedApiKey(row)) {
    throw new TopupError(
      "not-configured",
      "ต้องตั้งค่า EasySlip API Key ก่อนเปิด PromptPay QR",
      409,
    );
  }
}

export function updateTopupSettings(
  database: Database.Database,
  actor: Actor,
  input: unknown,
): AdminTopupSettingsResponse {
  const parsed = settingsUpdateSchema.safeParse(input);
  if (!parsed.success) {
    throw new TopupError("invalid-request", "ข้อมูลการตั้งค่าไม่ถูกต้อง", 400);
  }
  const data = parsed.data;
  const changedSections = [
    data.truemoney,
    data.promptpay,
    data.bankTransfer,
    data.coupon,
    data.slipProvider,
  ].filter((value) => value !== undefined);
  if (changedSections.length === 0) {
    throw new TopupError("invalid-request", "ไม่มีข้อมูลที่ต้องบันทึก", 400);
  }

  database
    .transaction(() => {
      const current = readSettingsRow(database);
      if (current.updated_at !== data.expectedUpdatedAt) {
        throw new TopupError(
          "settings-conflict",
          "ข้อมูลถูกแก้ไขจากที่อื่น กรุณารีเฟรชแล้วลองใหม่",
          409,
        );
      }

      let truemoneyEnabled = current.truemoney_enabled === 1;
      let truemoneyPhone = current.truemoney_phone;
      let promptpayEnabled = current.promptpay_enabled === 1;
      let promptpayType = current.promptpay_type;
      let promptpayNumber = current.promptpay_number;
      let promptpayAccountName = current.promptpay_account_name;
      let bankTransferEnabled = current.bank_transfer_enabled === 1;
      let couponEnabled = current.coupon_enabled === 1;
      let couponMessage = current.coupon_message;
      let slipProvider = current.slip_provider;

      if (data.truemoney) {
        truemoneyEnabled = data.truemoney.enabled;
        if (data.truemoney.phone !== undefined) {
          const phone = data.truemoney.phone
            ? normalizeDigits(data.truemoney.phone)
            : null;
          if (phone && !isValidPhoneNumber(phone)) {
            throw new TopupError(
              "invalid-request",
              "เบอร์โทรศัพท์ผู้รับซองอั่งเปาต้องเป็น 10 หลักขึ้นต้นด้วย 0",
              400,
            );
          }
          truemoneyPhone = phone;
        }
      }
      if (truemoneyEnabled && (!truemoneyPhone || !isValidPhoneNumber(truemoneyPhone))) {
        throw new TopupError(
          "invalid-request",
          "กรุณาตั้งค่าเบอร์โทรศัพท์ผู้รับซองอั่งเปาก่อนเปิดช่องทาง",
          400,
        );
      }

      if (data.promptpay) {
        promptpayEnabled = data.promptpay.enabled;
        if (data.promptpay.type !== undefined) {
          if (!isPromptpayType(data.promptpay.type)) {
            throw new TopupError("invalid-request", "ประเภท PromptPay ไม่ถูกต้อง", 400);
          }
          promptpayType = data.promptpay.type;
        }
        if (data.promptpay.number !== undefined) {
          promptpayNumber = data.promptpay.number
            ? validatePromptpay(promptpayType, data.promptpay.number)
            : null;
        }
        if (data.promptpay.accountName !== undefined) {
          promptpayAccountName = data.promptpay.accountName?.trim().slice(0, 120) || null;
        }
        if (promptpayEnabled) {
          if (!promptpayNumber) {
            throw new TopupError(
              "invalid-request",
              "กรุณาระบุหมายเลข PromptPay ก่อนเปิดช่องทาง",
              400,
            );
          }
          validatePromptpay(promptpayType, promptpayNumber);
          if (!promptpayAccountName) {
            throw new TopupError(
              "invalid-request",
              "กรุณาระบุชื่อบัญชีสำหรับตรวจสอบสลิป",
              400,
            );
          }
        }
      }

      if (data.bankTransfer) {
        bankTransferEnabled = data.bankTransfer.enabled;
      }

      // Only one bank rail may be enabled at a time.
      if (data.promptpay?.enabled === true && data.bankTransfer?.enabled !== true) {
        bankTransferEnabled = false;
      }
      if (data.bankTransfer?.enabled === true && data.promptpay?.enabled !== true) {
        promptpayEnabled = false;
      }
      if (promptpayEnabled && bankTransferEnabled) {
        throw new TopupError(
          "invalid-request",
          "เปิดได้ครั้งละหนึ่งรูปแบบ: PromptPay QR หรือโอนผ่านบัญชีธนาคาร",
          400,
        );
      }

      if (data.coupon) {
        couponEnabled = data.coupon.enabled;
        if (data.coupon.message !== undefined) {
          couponMessage =
            data.coupon.message.trim().slice(0, 200) || DEFAULT_COUPON_MESSAGE;
        }
      }

      if (data.slipProvider !== undefined) {
        if (!isSlipProvider(data.slipProvider)) {
          throw new TopupError("invalid-request", "ผู้ให้บริการตรวจสลิปไม่ถูกต้อง", 400);
        }
        if (
          data.slipProvider !== current.slip_provider &&
          countPendingSlipChannelTopups(database) > 0
        ) {
          throw new TopupError(
            "settings-conflict",
            "มีรายการรอตรวจสลิปอยู่ ไม่สามารถเปลี่ยนผู้ให้บริการได้จนกว่าจะตรวจเสร็จ",
            409,
          );
        }
        slipProvider = data.slipProvider;
      }

      if (promptpayEnabled) ensureEasySlipReady(database);
      if (promptpayEnabled || bankTransferEnabled) {
        ensureProviderReadyForSlip(database, slipProvider);
      }
      if (bankTransferEnabled && countActiveBankAccounts(database) === 0) {
        throw new TopupError(
          "invalid-request",
          "กรุณาเพิ่มบัญชีธนาคารที่เปิดใช้งานก่อนเปิดช่องทางโอน",
          400,
        );
      }

      const now = nowMs();
      const result = database
        .prepare(
          `UPDATE topup_settings SET
             truemoney_enabled = ?, truemoney_phone = ?,
             promptpay_enabled = ?, promptpay_type = ?, promptpay_number = ?,
             promptpay_account_name = ?, bank_transfer_enabled = ?,
             coupon_enabled = ?, coupon_message = ?, slip_provider = ?,
             updated_at = ?, updated_by = ?
           WHERE id = 1 AND updated_at = ?`,
        )
        .run(
          truemoneyEnabled ? 1 : 0,
          truemoneyPhone,
          promptpayEnabled ? 1 : 0,
          promptpayType,
          promptpayNumber,
          promptpayAccountName,
          bankTransferEnabled ? 1 : 0,
          couponEnabled ? 1 : 0,
          couponMessage,
          slipProvider,
          now,
          actor.id,
          data.expectedUpdatedAt,
        );
      if (result.changes !== 1) {
        throw new TopupError(
          "settings-conflict",
          "ข้อมูลถูกแก้ไขจากที่อื่น กรุณารีเฟรชแล้วลองใหม่",
          409,
        );
      }

      recordTopupAudit(database, actor, "settings.update", "settings", "1", {
        sections: Object.keys(data).filter((key) => key !== "expectedUpdatedAt"),
        truemoneyEnabled,
        promptpayEnabled,
        bankTransferEnabled,
        couponEnabled,
        slipProvider,
      });
    })
    .immediate();

  return getAdminTopupSettings(database);
}

const providerUpdateSchema = z
  .object({
    expectedUpdatedAt: z.number().int().nonnegative(),
    apiKey: z.string().min(8).max(300).optional(),
    clearApiKey: z.boolean().optional(),
    branchId: z.string().max(64).nullable().optional(),
  })
  .strict();

const EASYSLIP_KEY_PATTERN =
  /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i;
const SLIPOK_BRANCH_PATTERN = /^[A-Za-z0-9_-]{1,64}$/;

export function updateTopupProvider(
  database: Database.Database,
  actor: Actor,
  provider: SlipProvider,
  input: unknown,
): AdminTopupSettingsResponse {
  if (!isSlipProvider(provider)) {
    throw new TopupError("invalid-request", "ผู้ให้บริการไม่ถูกต้อง", 400);
  }
  const parsed = providerUpdateSchema.safeParse(input);
  if (!parsed.success) {
    throw new TopupError("invalid-request", "ข้อมูลผู้ให้บริการไม่ถูกต้อง", 400);
  }
  const data = parsed.data;
  if (data.apiKey !== undefined && data.clearApiKey === true) {
    throw new TopupError(
      "invalid-request",
      "เลือกได้อย่างเดียวระหว่างบันทึกคีย์ใหม่หรือลบคีย์",
      400,
    );
  }
  if (
    data.apiKey === undefined &&
    data.clearApiKey !== true &&
    data.branchId === undefined
  ) {
    throw new TopupError("invalid-request", "ไม่มีข้อมูลที่ต้องบันทึก", 400);
  }

  database
    .transaction(() => {
      const row = readProviderRow(database, provider);
      if (row.updated_at !== data.expectedUpdatedAt) {
        throw new TopupError(
          "settings-conflict",
          "ข้อมูลถูกแก้ไขจากที่อื่น กรุณารีเฟรชแล้วลองใหม่",
          409,
        );
      }
      let apiKeyEnc = row.api_key_enc;
      let branchId = row.branch_id;

      if (data.clearApiKey === true) {
        if (
          row.provider === readSettingsRow(database).slip_provider &&
          countPendingSlipChannelTopups(database) > 0
        ) {
          throw new TopupError(
            "settings-conflict",
            "มีรายการรอตรวจสลิปอยู่ ไม่สามารถลบคีย์ที่ใช้งานอยู่ได้",
            409,
          );
        }
        apiKeyEnc = null;
      }
      if (data.apiKey !== undefined) {
        const key = data.apiKey.trim();
        if (provider === "easyslip" && !EASYSLIP_KEY_PATTERN.test(key)) {
          throw new TopupError(
            "invalid-request",
            "EasySlip API Key ต้องเป็นรูปแบบ UUID",
            400,
          );
        }
        if (provider === "slipok" && /\s/.test(key)) {
          throw new TopupError(
            "invalid-request",
            "SlipOk API Key ต้องไม่มีช่องว่าง",
            400,
          );
        }
        apiKeyEnc = encryptSecret(key);
      }
      if (data.branchId !== undefined) {
        if (provider === "easyslip") {
          throw new TopupError(
            "invalid-request",
            "EasySlip ไม่มีค่า Branch ID",
            400,
          );
        }
        const value = data.branchId?.trim() || null;
        if (value && !SLIPOK_BRANCH_PATTERN.test(value)) {
          throw new TopupError(
            "invalid-request",
            "SlipOk Branch ID ต้องเป็นตัวอักษรหรือตัวเลข 1-64 ตัว",
            400,
          );
        }
        branchId = value;
      }
      const now = nowMs();
      const result = database
        .prepare(
          `UPDATE topup_provider_settings SET api_key_enc = ?, branch_id = ?,
             updated_at = ?, updated_by = ?
           WHERE provider = ? AND updated_at = ?`,
        )
        .run(apiKeyEnc, branchId, now, actor.id, provider, data.expectedUpdatedAt);
      if (result.changes !== 1) {
        throw new TopupError(
          "settings-conflict",
          "ข้อมูลถูกแก้ไขจากที่อื่น กรุณารีเฟรชแล้วลองใหม่",
          409,
        );
      }
      recordTopupAudit(database, actor, "provider.update", "provider", provider, {
        apiKey: data.apiKey !== undefined ? "set" : data.clearApiKey ? "cleared" : "unchanged",
        branchId,
      });
    })
    .immediate();

  return getAdminTopupSettings(database);
}

function recordProviderTest(
  database: Database.Database,
  actor: Actor,
  provider: SlipProvider,
  ok: boolean,
  message: string,
) {
  const now = nowMs();
  database
    .prepare(
      `UPDATE topup_provider_settings SET last_test_at = ?, last_test_ok = ?,
         last_test_message = ?, updated_by = ?
       WHERE provider = ?`,
    )
    .run(now, ok ? 1 : 0, message.slice(0, 300), actor.id, provider);
  recordTopupAudit(database, actor, "provider.test", "provider", provider, {
    ok,
    message: message.slice(0, 300),
  });
}

export async function testTopupProvider(
  database: Database.Database,
  actor: Actor,
  provider: SlipProvider,
): Promise<ProviderTestResult> {
  if (!isSlipProvider(provider)) {
    throw new TopupError("invalid-request", "ผู้ให้บริการไม่ถูกต้อง", 400);
  }
  const credentials = getProviderCredentials(database, provider);
  if (!credentials) {
    throw new TopupError(
      "not-configured",
      "ยังไม่ได้ตั้งค่า API Key ของผู้ให้บริการนี้",
      409,
    );
  }

  let result: ProviderTestResult;
  if (provider === "easyslip") {
    const test = await easySlipTestConnection(credentials.apiKey);
    if (test.kind === "ok") {
      const detail: Record<string, string | number | null> = {
        remainingQuota: test.data.remainingQuota ?? null,
        usedQuota: test.data.usedQuota ?? null,
        maxQuota: test.data.maxQuota ?? null,
        expiredAt: test.data.expiredAt ?? null,
      };
      result = {
        provider,
        ok: true,
        message: "เชื่อมต่อ EasySlip สำเร็จ",
        detail,
        testedAt: nowMs(),
      };
    } else {
      result = {
        provider,
        ok: false,
        message: test.message,
        detail: {
          code: test.code,
          httpStatus: test.httpStatus,
        },
        testedAt: nowMs(),
      };
    }
  } else {
    const test = await slipOkTestConnection(
      credentials.apiKey,
      credentials.branchId ?? "",
    );
    if (test.kind === "ok") {
      result = {
        provider,
        ok: true,
        message: "เชื่อมต่อ SlipOk สำเร็จ",
        detail: {
          quota: test.data.quota ?? null,
          overQuota: test.data.overQuota ?? null,
        },
        testedAt: nowMs(),
      };
    } else {
      result = {
        provider,
        ok: false,
        message: test.message,
        detail: {
          code: test.code,
          httpStatus: test.httpStatus,
        },
        testedAt: nowMs(),
      };
    }
  }
  recordProviderTest(database, actor, provider, result.ok, result.message);
  return result;
}

// ---------------------------------------------------------------------------
// Bank accounts
// ---------------------------------------------------------------------------

const bankAccountSchema = z
  .object({
    bankCode: z.string().trim().regex(/^\d{3}$/, "Bank Code ต้องเป็นตัวเลข 3 หลัก"),
    bankName: z.string().trim().min(2).max(100),
    accountNumber: z.string().trim().min(8).max(24),
    accountName: z.string().trim().min(2).max(120),
    logoKey: z.string().trim().max(32).nullable().optional(),
    logoDataUrl: z.string().max(100_000).nullable().optional(),
    isActive: z.boolean().optional(),
  })
  .strict();

const bankAccountUpdateSchema = bankAccountSchema.extend({
  expectedUpdatedAt: z.number().int().nonnegative(),
});

function detectImageMime(bytes: Uint8Array): string | null {
  if (
    bytes.length >= 8 &&
    bytes[0] === 0x89 &&
    bytes[1] === 0x50 &&
    bytes[2] === 0x4e &&
    bytes[3] === 0x47 &&
    bytes[4] === 0x0d &&
    bytes[5] === 0x0a &&
    bytes[6] === 0x1a &&
    bytes[7] === 0x0a
  ) {
    return "image/png";
  }
  if (bytes.length >= 3 && bytes[0] === 0xff && bytes[1] === 0xd8 && bytes[2] === 0xff) {
    return "image/jpeg";
  }
  if (
    bytes.length >= 12 &&
    bytes[0] === 0x52 &&
    bytes[1] === 0x49 &&
    bytes[2] === 0x46 &&
    bytes[3] === 0x46 &&
    bytes[8] === 0x57 &&
    bytes[9] === 0x45 &&
    bytes[10] === 0x42 &&
    bytes[11] === 0x50
  ) {
    return "image/webp";
  }
  return null;
}

function validateLogo(
  logoKey: string | null | undefined,
  logoDataUrl: string | null | undefined,
): { logoKey: string | null; logoDataUrl: string | null } {
  const key = logoKey?.trim() || null;
  const dataUrl = logoDataUrl?.trim() || null;
  if (key && dataUrl) {
    throw new TopupError(
      "invalid-request",
      "เลือกได้อย่างเดียวระหว่างโลโก้สำเร็จรูปหรืออัปโหลดรูป",
      400,
    );
  }
  if (key) {
    if (!isKnownBankLogoKey(key)) {
      throw new TopupError("invalid-request", "ไม่พบโลโก้ธนาคารที่เลือก", 400);
    }
    return { logoKey: key, logoDataUrl: null };
  }
  if (!dataUrl) {
    throw new TopupError(
      "invalid-request",
      "กรุณาเลือกหรืออัปโหลดโลโก้ธนาคาร",
      400,
    );
  }
  const match = /^data:(image\/(?:png|jpeg|webp));base64,([A-Za-z0-9+/=]+)$/.exec(
    dataUrl,
  );
  if (!match) {
    throw new TopupError(
      "unsupported-file",
      "โลโก้ต้องเป็นรูป PNG, JPEG หรือ WebP",
      400,
    );
  }
  const declaredMime = match[1];
  const bytes = Buffer.from(match[2], "base64");
  if (bytes.length > MAX_BANK_LOGO_BYTES) {
    throw new TopupError("file-too-large", "โลโก้ต้องมีขนาดไม่เกิน 32 KB", 413);
  }
  const detected = detectImageMime(bytes);
  if (!detected || detected !== declaredMime) {
    throw new TopupError("unsupported-file", "ไฟล์โลโก้ไม่ถูกต้อง", 400);
  }
  return { logoKey: null, logoDataUrl: `data:${detected};base64,${match[2]}` };
}

function normalizeBankInput(input: unknown) {
  const parsed = bankAccountSchema.safeParse(input);
  if (!parsed.success) {
    const message =
      parsed.error.issues[0]?.message ?? "ข้อมูลบัญชีธนาคารไม่ถูกต้อง";
    throw new TopupError("invalid-request", message, 400);
  }
  const data = parsed.data;
  const accountNumber = normalizeDigits(data.accountNumber);
  if (!isValidBankAccountNumber(accountNumber)) {
    throw new TopupError(
      "invalid-request",
      "เลขบัญชีธนาคารต้องเป็นตัวเลข 8-15 หลัก",
      400,
    );
  }
  const logo = validateLogo(data.logoKey, data.logoDataUrl);
  return {
    bankCode: normalizeDigits(data.bankCode),
    bankName: data.bankName,
    accountNumber,
    accountName: data.accountName,
    ...logo,
    isActive: data.isActive ?? true,
  };
}

export function createBankAccount(
  database: Database.Database,
  actor: Actor,
  input: unknown,
): BankAccountAdmin {
  const data = normalizeBankInput(input);
  ensureTopupSchema(database);
  const now = nowMs();
  const id = randomUUID();
  try {
    database
      .transaction(() => {
        database
          .prepare(
            `INSERT INTO topup_bank_accounts
             (id, bank_code, bank_name, account_number, account_name, logo_key,
              logo_data_url, is_active, archived_at, created_at, updated_at)
             VALUES (?,?,?,?,?,?,?,?,NULL,?,?)`,
          )
          .run(
            id,
            data.bankCode,
            data.bankName,
            data.accountNumber,
            data.accountName,
            data.logoKey,
            data.logoDataUrl,
            data.isActive ? 1 : 0,
            now,
            now,
          );
        recordTopupAudit(database, actor, "bank_account.create", "bank_account", id, {
          bankCode: data.bankCode,
          bankName: data.bankName,
          accountNumber: data.accountNumber,
          accountName: data.accountName,
          isActive: data.isActive,
        });
      })
      .immediate();
  } catch (error) {
    if (
      error instanceof Error &&
      /UNIQUE constraint failed/i.test(error.message)
    ) {
      throw new TopupError(
        "invalid-request",
        "บัญชีธนาคารนี้มีอยู่แล้ว",
        409,
      );
    }
    throw error;
  }
  const row = getBankAccountRow(database, id);
  if (!row) throw new TopupError("internal-error", "บันทึกบัญชีไม่สำเร็จ", 500);
  return bankAccountFromRow(row);
}

export function updateBankAccount(
  database: Database.Database,
  actor: Actor,
  accountId: string,
  input: unknown,
): BankAccountAdmin {
  const parsed = bankAccountUpdateSchema.safeParse(input);
  if (!parsed.success) {
    throw new TopupError("invalid-request", "ข้อมูลบัญชีธนาคารไม่ถูกต้อง", 400);
  }
  const expectedUpdatedAt = parsed.data.expectedUpdatedAt;
  const bankInput = {
    bankCode: parsed.data.bankCode,
    bankName: parsed.data.bankName,
    accountNumber: parsed.data.accountNumber,
    accountName: parsed.data.accountName,
    logoKey: parsed.data.logoKey,
    logoDataUrl: parsed.data.logoDataUrl,
    isActive: parsed.data.isActive,
  };
  const data = normalizeBankInput(bankInput);

  try {
    database
      .transaction(() => {
        const row = getBankAccountRow(database, accountId);
        if (!row || row.archived_at !== null) {
          throw new TopupError("transaction-not-found", "ไม่พบบัญชีธนาคาร", 404);
        }
        if (row.updated_at !== expectedUpdatedAt) {
        throw new TopupError(
          "settings-conflict",
          "ข้อมูลถูกแก้ไขจากที่อื่น กรุณารีเฟรชแล้วลองใหม่",
          409,
        );
      }
      if (
        row.is_active === 1 &&
        !data.isActive &&
        readSettingsRow(database).bank_transfer_enabled === 1 &&
        countActiveBankAccounts(database) <= 1
      ) {
        throw new TopupError(
          "last-bank-account",
          "ต้องมีบัญชีที่เปิดใช้งานอย่างน้อยหนึ่งบัญชีเมื่อเปิดช่องทางโอน",
          409,
        );
      }
      const now = nowMs();
      const result = database
        .prepare(
          `UPDATE topup_bank_accounts SET bank_code = ?, bank_name = ?,
             account_number = ?, account_name = ?, logo_key = ?, logo_data_url = ?,
             is_active = ?, updated_at = ?
           WHERE id = ? AND updated_at = ? AND archived_at IS NULL`,
        )
        .run(
          data.bankCode,
          data.bankName,
          data.accountNumber,
          data.accountName,
          data.logoKey,
          data.logoDataUrl,
          data.isActive ? 1 : 0,
          now,
          accountId,
          expectedUpdatedAt,
        );
      if (result.changes !== 1) {
        throw new TopupError(
          "settings-conflict",
          "ข้อมูลถูกแก้ไขจากที่อื่น กรุณารีเฟรชแล้วลองใหม่",
          409,
        );
      }
        recordTopupAudit(database, actor, "bank_account.update", "bank_account", accountId, {
          bankCode: data.bankCode,
          bankName: data.bankName,
          accountNumber: data.accountNumber,
          accountName: data.accountName,
          isActive: data.isActive,
        });
      })
      .immediate();
  } catch (error) {
    if (error instanceof Error && /UNIQUE constraint failed/i.test(error.message)) {
      throw new TopupError("invalid-request", "บัญชีธนาคารนี้มีอยู่แล้ว", 409);
    }
    throw error;
  }

  const row = getBankAccountRow(database, accountId);
  if (!row) throw new TopupError("internal-error", "บันทึกบัญชีไม่สำเร็จ", 500);
  return bankAccountFromRow(row);
}

export function archiveBankAccount(
  database: Database.Database,
  actor: Actor,
  accountId: string,
): BankAccountAdmin {
  database
    .transaction(() => {
      const row = getBankAccountRow(database, accountId);
      if (!row) {
        throw new TopupError("transaction-not-found", "ไม่พบบัญชีธนาคาร", 404);
      }
      if (row.archived_at !== null) return;
      if (
        row.is_active === 1 &&
        readSettingsRow(database).bank_transfer_enabled === 1 &&
        countActiveBankAccounts(database) <= 1
      ) {
        throw new TopupError(
          "last-bank-account",
          "ต้องมีบัญชีที่เปิดใช้งานอย่างน้อยหนึ่งบัญชีเมื่อเปิดช่องทางโอน",
          409,
        );
      }
      const now = nowMs();
      database
        .prepare(
          `UPDATE topup_bank_accounts SET archived_at = ?, is_active = 0, updated_at = ?
           WHERE id = ? AND archived_at IS NULL`,
        )
        .run(now, now, accountId);
      recordTopupAudit(database, actor, "bank_account.archive", "bank_account", accountId, {
        archived: true,
      });
    })
    .immediate();
  const row = getBankAccountRow(database, accountId);
  if (!row) throw new TopupError("internal-error", "ปิดใช้งานบัญชีไม่สำเร็จ", 500);
  return bankAccountFromRow(row);
}
