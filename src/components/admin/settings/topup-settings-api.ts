/**
 * Client-safe helpers for the admin top-up settings page.
 *
 * The page never receives provider secrets: the aggregate API only exposes
 * `maskedKey`/`configured`. Requests still go through the same-origin admin
 * routes and every response shape here mirrors `src/lib/topup/types.ts`.
 */

import {
  isValidBankAccountNumber,
  isValidNatId,
  isValidPhoneNumber,
} from "@/lib/topup/amount";
import {
  MAX_BANK_LOGO_BYTES,
  type BankRail,
  type PromptpayType,
  type SlipProvider,
} from "@/lib/topup/constants";
import type {
  AdminTopupSettingsResponse,
  BankAccountAdmin,
  ProviderAdminStatus,
  ProviderTestResult,
  TopupSettingsAdmin,
  TopupSettingsAuditEntry,
} from "@/lib/topup/types";

export const TOPUP_SETTINGS_ENDPOINT = "/api/admin/topup-settings";
export const TOPUP_BANK_ACCOUNTS_ENDPOINT = "/api/admin/topup-bank-accounts";

export function topupProviderEndpoint(provider: SlipProvider) {
  return `/api/admin/topup-providers/${provider}`;
}

export function topupProviderTestEndpoint(provider: SlipProvider) {
  return `/api/admin/topup-providers/${provider}/test`;
}

export function topupBankAccountEndpoint(accountId: string) {
  return `${TOPUP_BANK_ACCOUNTS_ENDPOINT}/${encodeURIComponent(accountId)}`;
}

// ---------------------------------------------------------------------------
// Errors
// ---------------------------------------------------------------------------

export class TopupApiError extends Error {
  readonly status: number;
  readonly code: string;

  constructor(status: number, code: string, message: string) {
    super(message);
    this.name = "TopupApiError";
    this.status = status;
    this.code = code;
  }

  get isConflict() {
    return this.status === 409;
  }
}

export type TopupMutationResult<T> =
  | { ok: true; data: T }
  | { ok: false; error: TopupApiError };

function isRecord(value: unknown): value is Record<string, unknown> {
  return Boolean(value) && typeof value === "object" && !Array.isArray(value);
}

function defaultHttpErrorMessage(status: number) {
  if (status === 401) return "กรุณาเข้าสู่ระบบใหม่";
  if (status === 403) return "คุณไม่มีสิทธิ์เข้าถึงข้อมูลผู้ดูแลระบบ";
  if (status === 404) return "ไม่พบข้อมูลที่ต้องการ";
  if (status === 409) return "ข้อมูลถูกแก้ไขจากที่อื่น กรุณารีเฟรช";
  if (status === 413) return "ไฟล์มีขนาดใหญ่เกินกำหนด";
  if (status >= 500) return "เซิร์ฟเวอร์ขัดข้อง กรุณาลองใหม่";
  return `ดำเนินการไม่สำเร็จ (${status})`;
}

/** Thai message for any thrown request error; 409 always opens the conflict banner. */
export function topupErrorMessage(error: unknown): string {
  if (error instanceof TopupApiError && error.status === 409) {
    return "ข้อมูลถูกแก้ไขจากที่อื่น กรุณารีเฟรช";
  }
  if (error instanceof TopupApiError && error.message) return error.message;
  if (error instanceof Error && error.message) return error.message;
  return "ดำเนินการไม่สำเร็จ กรุณาลองใหม่";
}

export function asTopupApiError(error: unknown): TopupApiError {
  if (error instanceof TopupApiError) return error;
  return new TopupApiError(
    0,
    "network-error",
    error instanceof Error && error.message
      ? error.message
      : "การเชื่อมต่อขัดข้อง กรุณาตรวจสอบเครือข่ายแล้วลองใหม่",
  );
}

async function requestJson<T>(url: string, init?: RequestInit): Promise<T> {
  const response = await fetch(url, { cache: "no-store", ...init });
  let payload: unknown = null;
  try {
    payload = await response.json();
  } catch {
    payload = null;
  }
  if (!response.ok) {
    const record = isRecord(payload) ? payload : {};
    const message =
      typeof record.error === "string" && record.error.trim()
        ? record.error
        : defaultHttpErrorMessage(response.status);
    const code = typeof record.code === "string" ? record.code : "";
    throw new TopupApiError(response.status, code, message);
  }
  if (payload === null) {
    throw new TopupApiError(
      response.status,
      "invalid-response",
      "รูปแบบข้อมูลตอบกลับไม่ถูกต้อง",
    );
  }
  return payload as T;
}

// ---------------------------------------------------------------------------
// Aggregate settings
// ---------------------------------------------------------------------------

export async function loadAdminTopupSettings(): Promise<AdminTopupSettingsResponse> {
  return requestJson<AdminTopupSettingsResponse>(TOPUP_SETTINGS_ENDPOINT);
}

export type SettingsUpdatePayload = {
  expectedUpdatedAt: number;
  truemoney?: { enabled: boolean; phone?: string | null };
  promptpay?: {
    enabled: boolean;
    type?: PromptpayType;
    number?: string | null;
    accountName?: string | null;
  };
  bankTransfer?: { enabled: boolean };
  coupon?: { enabled: boolean; message?: string };
  slipProvider?: SlipProvider;
};

export async function updateAdminTopupSettings(
  payload: SettingsUpdatePayload,
): Promise<AdminTopupSettingsResponse> {
  return requestJson<AdminTopupSettingsResponse>(TOPUP_SETTINGS_ENDPOINT, {
    method: "PUT",
    headers: { "Content-Type": "application/json" },
    body: JSON.stringify(payload),
  });
}

// ---------------------------------------------------------------------------
// Slip providers
// ---------------------------------------------------------------------------

export type ProviderUpdatePayload = {
  expectedUpdatedAt: number;
  apiKey?: string;
  clearApiKey?: boolean;
  branchId?: string | null;
};

export async function updateAdminTopupProvider(
  provider: SlipProvider,
  payload: ProviderUpdatePayload,
): Promise<AdminTopupSettingsResponse> {
  return requestJson<AdminTopupSettingsResponse>(
    topupProviderEndpoint(provider),
    {
      method: "PUT",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify(payload),
    },
  );
}

export async function testAdminTopupProvider(
  provider: SlipProvider,
): Promise<ProviderTestResult> {
  return requestJson<ProviderTestResult>(topupProviderTestEndpoint(provider), {
    method: "POST",
  });
}

// ---------------------------------------------------------------------------
// Bank accounts
// ---------------------------------------------------------------------------

export type BankAccountPayload = {
  bankCode: string;
  bankName: string;
  accountNumber: string;
  accountName: string;
  logoKey: string | null;
  logoDataUrl: string | null;
  isActive: boolean;
};

type BankAccountResponse = { bankAccount: BankAccountAdmin };
type BankAccountsResponse = { bankAccounts: BankAccountAdmin[] };

export async function fetchAdminBankAccounts(): Promise<BankAccountAdmin[]> {
  const response = await requestJson<BankAccountsResponse>(
    TOPUP_BANK_ACCOUNTS_ENDPOINT,
  );
  return response.bankAccounts;
}

export async function createAdminBankAccount(
  payload: BankAccountPayload,
): Promise<BankAccountAdmin> {
  const response = await requestJson<BankAccountResponse>(
    TOPUP_BANK_ACCOUNTS_ENDPOINT,
    {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify(payload),
    },
  );
  return response.bankAccount;
}

export async function updateAdminBankAccount(
  accountId: string,
  payload: BankAccountPayload & { expectedUpdatedAt: number },
): Promise<BankAccountAdmin> {
  const response = await requestJson<BankAccountResponse>(
    topupBankAccountEndpoint(accountId),
    {
      method: "PATCH",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify(payload),
    },
  );
  return response.bankAccount;
}

export async function archiveAdminBankAccount(
  accountId: string,
): Promise<BankAccountAdmin> {
  const response = await requestJson<BankAccountResponse>(
    topupBankAccountEndpoint(accountId),
    { method: "DELETE" },
  );
  return response.bankAccount;
}

/** Mirrors the server's list order: active first, then newest. */
export function sortBankAccounts(
  accounts: readonly BankAccountAdmin[],
): BankAccountAdmin[] {
  return [...accounts].sort((left, right) => {
    const leftArchived = left.archivedAt !== null;
    const rightArchived = right.archivedAt !== null;
    if (leftArchived !== rightArchived) return leftArchived ? 1 : -1;
    if (left.isActive !== right.isActive) return left.isActive ? -1 : 1;
    return right.createdAt - left.createdAt;
  });
}

export function mergeBankAccount(
  accounts: readonly BankAccountAdmin[],
  account: BankAccountAdmin,
): BankAccountAdmin[] {
  const exists = accounts.some((item) => item.id === account.id);
  const next = exists
    ? accounts.map((item) => (item.id === account.id ? account : item))
    : [account, ...accounts];
  return sortBankAccounts(next);
}

// ---------------------------------------------------------------------------
// Form drafts and validation (mirrors src/lib/topup/settings.ts basics)
// ---------------------------------------------------------------------------

export type SettingsDraft = {
  truemoney: { enabled: boolean; phone: string };
  promptpay: {
    enabled: boolean;
    type: PromptpayType;
    number: string;
    accountName: string;
  };
  bankTransfer: { enabled: boolean };
  coupon: { enabled: boolean; message: string };
  slipProvider: SlipProvider;
};

export function settingsDraftFromAdmin(settings: TopupSettingsAdmin): SettingsDraft {
  return {
    truemoney: {
      enabled: settings.truemoney.enabled,
      phone: settings.truemoney.phone ?? "",
    },
    promptpay: {
      enabled: settings.promptpay.enabled,
      type: settings.promptpay.type,
      number: settings.promptpay.number ?? "",
      accountName: settings.promptpay.accountName ?? "",
    },
    bankTransfer: { enabled: settings.bankTransfer.enabled },
    coupon: {
      enabled: settings.coupon.enabled,
      message: settings.coupon.message,
    },
    slipProvider: settings.slipProvider,
  };
}

export function normalizeDigits(value: string): string {
  return value.replace(/[\s-]/g, "");
}

export type FieldErrors = Record<string, string | undefined>;

export function validateTrueMoney(draft: SettingsDraft["truemoney"]): FieldErrors {
  const errors: FieldErrors = {};
  const phone = normalizeDigits(draft.phone);
  if (phone && !isValidPhoneNumber(phone)) {
    errors.phone = "เบอร์โทรศัพท์ผู้รับซองอั่งเปาต้องเป็น 10 หลักขึ้นต้นด้วย 0";
  } else if (draft.enabled && !phone) {
    errors.phone = "กรุณากรอกเบอร์โทรศัพท์ผู้รับซองอั่งเปาก่อนเปิดช่องทาง";
  }
  return errors;
}

export function promptpayValidationMessage(
  type: PromptpayType,
): string {
  return type === "msisdn"
    ? "หมายเลข PromptPay ต้องเป็นเบอร์โทรศัพท์ 10 หลักขึ้นต้นด้วย 0"
    : "หมายเลข PromptPay ต้องเป็นเลขบัตรประชาชน 13 หลัก";
}

export function validatePromptPay(
  draft: SettingsDraft["promptpay"],
  options: { easySlipConfigured: boolean },
): FieldErrors {
  const errors: FieldErrors = {};
  const number = normalizeDigits(draft.number);
  const numberValid =
    draft.type === "msisdn" ? isValidPhoneNumber(number) : isValidNatId(number);
  const accountName = draft.accountName.trim();

  if (number && !numberValid) {
    errors.number = promptpayValidationMessage(draft.type);
  } else if (draft.enabled && !number) {
    errors.number = "กรุณาระบุหมายเลข PromptPay ก่อนเปิดช่องทาง";
  }
  if (accountName.length > 120) {
    errors.accountName = "ชื่อบัญชีต้องไม่เกิน 120 ตัวอักษร";
  } else if (draft.enabled && !accountName) {
    errors.accountName = "กรุณาระบุชื่อบัญชีสำหรับตรวจสอบสลิป";
  }
  if (draft.enabled && !options.easySlipConfigured) {
    errors.form = "ต้องตั้งค่า EasySlip API Key ก่อนเปิด PromptPay QR";
  }
  return errors;
}

export function validateCoupon(message: string): FieldErrors {
  const errors: FieldErrors = {};
  if (message.length > 200) {
    errors.message = "ข้อความคูปองต้องไม่เกิน 200 ตัวอักษร";
  }
  return errors;
}

/** Same UUID shape enforced by the server for EasySlip API keys. */
export const EASYSLIP_KEY_PATTERN =
  /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i;
export const SLIPOK_BRANCH_PATTERN = /^[A-Za-z0-9_-]{1,64}$/;

export function validateProviderSave(
  provider: SlipProvider,
  values: { apiKey: string; branchId: string },
): FieldErrors {
  const errors: FieldErrors = {};
  const apiKey = values.apiKey.trim();
  if (provider === "easyslip") {
    if (!apiKey) {
      errors.apiKey = "กรอก EasySlip API Key ใหม่เพื่อบันทึก";
    } else if (!EASYSLIP_KEY_PATTERN.test(apiKey)) {
      errors.apiKey = "EasySlip API Key ต้องเป็นรูปแบบ UUID";
    }
    return errors;
  }
  if (apiKey && /\s/.test(apiKey)) {
    errors.apiKey = "SlipOk API Key ต้องไม่มีช่องว่าง";
  }
  const branchId = values.branchId.trim();
  if (!branchId) {
    errors.branchId = "กรุณาระบุ Branch ID ก่อนเปิดใช้งาน SlipOk";
  } else if (!SLIPOK_BRANCH_PATTERN.test(branchId)) {
    errors.branchId = "SlipOk Branch ID ต้องเป็นตัวอักษรหรือตัวเลข 1-64 ตัว";
  }
  return errors;
}

export type BankAccountFormValues = {
  bankCode: string;
  bankName: string;
  accountNumber: string;
  accountName: string;
  logoKey: string | null;
  logoDataUrl: string | null;
  isActive: boolean;
};

export function validateBankAccountForm(
  values: BankAccountFormValues,
): FieldErrors {
  const errors: FieldErrors = {};
  if (!/^\d{3}$/.test(values.bankCode.trim())) {
    errors.bankCode = "รหัสธนาคารต้องเป็นตัวเลข 3 หลัก";
  }
  if (values.bankName.trim().length < 2) {
    errors.bankName = "กรอกชื่อธนาคารอย่างน้อย 2 ตัวอักษร";
  } else if (values.bankName.trim().length > 100) {
    errors.bankName = "ชื่อธนาคารต้องไม่เกิน 100 ตัวอักษร";
  }
  if (!isValidBankAccountNumber(values.accountNumber)) {
    errors.accountNumber = "เลขที่บัญชีต้องเป็นตัวเลข 8-15 หลัก";
  }
  const accountName = values.accountName.trim();
  if (accountName.length < 2) {
    errors.accountName = "กรอกชื่อบัญชีอย่างน้อย 2 ตัวอักษร";
  } else if (accountName.length > 120) {
    errors.accountName = "ชื่อบัญชีต้องไม่เกิน 120 ตัวอักษร";
  }
  if (!values.logoKey && !values.logoDataUrl) {
    errors.logo = "กรุณาเลือกหรืออัปโหลดโลโก้ธนาคาร";
  }
  return errors;
}

export function hasFieldErrors(errors: FieldErrors): boolean {
  return Object.values(errors).some(Boolean);
}

// ---------------------------------------------------------------------------
// Display helpers
// ---------------------------------------------------------------------------

const BANGKOK_DATE_TIME = new Intl.DateTimeFormat("th-TH", {
  timeZone: "Asia/Bangkok",
  dateStyle: "medium",
  timeStyle: "short",
});

export function formatAdminDateTime(ms: number | null | undefined): string {
  if (ms === null || ms === undefined || !Number.isFinite(ms)) return "—";
  return BANGKOK_DATE_TIME.format(ms);
}

export const PROVIDER_LABELS: Record<SlipProvider, string> = {
  easyslip: "EasySlip",
  slipok: "SlipOk",
};

export function providerLabel(provider: SlipProvider): string {
  return PROVIDER_LABELS[provider];
}

export function formatPhoneNumber(value: string): string {
  const digits = normalizeDigits(value);
  if (digits.length !== 10) return value.trim() || "—";
  return `${digits.slice(0, 3)}-${digits.slice(3, 6)}-${digits.slice(6)}`;
}

export function formatPromptpayNumber(
  type: PromptpayType,
  value: string | null | undefined,
): string {
  const digits = normalizeDigits(value ?? "");
  if (!digits) return "—";
  if (type === "msisdn") return formatPhoneNumber(digits);
  if (digits.length !== 13) return digits;
  return `${digits.slice(0, 1)}-${digits.slice(1, 5)}-${digits.slice(5, 10)}-${digits.slice(10, 12)}-${digits.slice(12)}`;
}

export function formatAccountNumber(value: string): string {
  const digits = normalizeDigits(value);
  if (!digits) return value.trim() || "—";
  const groups = digits.match(/.{1,3}/g) ?? [digits];
  return groups.join("-");
}

const RAIL_LABELS: Record<BankRail, string> = {
  promptpay_qr: "PromptPay QR",
  bank_transfer: "โอนผ่านบัญชีธนาคาร",
};

export function railLabel(rail: BankRail): string {
  return RAIL_LABELS[rail];
}

/** One bank rail at a time: PromptPay wins if legacy data has both enabled. */
export function activeBankRail(settings: TopupSettingsAdmin): BankRail | null {
  if (settings.promptpay.enabled) return "promptpay_qr";
  if (settings.bankTransfer.enabled) return "bank_transfer";
  return null;
}

export function activeBankAccounts(
  accounts: readonly BankAccountAdmin[],
): BankAccountAdmin[] {
  return accounts.filter(
    (account) => account.archivedAt === null && account.isActive,
  );
}

const AUDIT_ACTION_LABELS: Record<string, string> = {
  "settings.update": "บันทึกการตั้งค่าเติมเงิน",
  "provider.update": "อัปเดตข้อมูลผู้ให้บริการตรวจสลิป",
  "provider.test": "ทดสอบการเชื่อมต่อผู้ให้บริการ",
  "bank_account.create": "เพิ่มบัญชีธนาคาร",
  "bank_account.update": "แก้ไขบัญชีธนาคาร",
  "bank_account.archive": "เก็บถาวรบัญชีธนาคาร",
};

export function auditActionLabel(action: string): string {
  return AUDIT_ACTION_LABELS[action] ?? action;
}

export function auditTargetLabel(entry: TopupSettingsAuditEntry): string {
  switch (entry.targetType) {
    case "settings":
      return "การตั้งค่าเติมเงิน";
    case "provider":
      return entry.targetId === "easyslip" || entry.targetId === "slipok"
        ? `ผู้ให้บริการ ${providerLabel(entry.targetId)}`
        : "ผู้ให้บริการตรวจสลิป";
    case "bank_account":
      return "บัญชีธนาคาร";
    default:
      return entry.targetType || "—";
  }
}

export function latestAuditEntries(
  entries: readonly TopupSettingsAuditEntry[],
): TopupSettingsAuditEntry[] {
  return [...entries]
    .sort((left, right) => right.createdAt - left.createdAt)
    .slice(0, 10);
}

export const MAX_LOGO_BYTES = MAX_BANK_LOGO_BYTES;
export const LOGO_FILE_ACCEPT = "image/png,image/jpeg,image/webp";

/** Every warning is defensive UI only — the API already blocks broken states. */
export function buildTopupWarnings(data: AdminTopupSettingsResponse): string[] {
  const warnings: string[] = [];
  const settings = data.settings;
  const selectedProvider = data.providers[settings.slipProvider];

  if (
    settings.truemoney.enabled &&
    !isValidPhoneNumber(normalizeDigits(settings.truemoney.phone ?? ""))
  ) {
    warnings.push(
      "TrueMoney เปิดใช้งานอยู่แต่ยังไม่มีเบอร์โทรศัพท์ผู้รับซองอั่งเปาที่ถูกต้อง",
    );
  }
  if (settings.promptpay.enabled) {
    if (!settings.promptpay.number) {
      warnings.push("PromptPay QR เปิดใช้งานอยู่แต่ยังไม่มีหมายเลข PromptPay");
    }
    if (!settings.promptpay.accountName) {
      warnings.push(
        "PromptPay QR เปิดใช้งานอยู่แต่ยังไม่มีชื่อบัญชีสำหรับตรวจสอบสลิป",
      );
    }
    if (!data.providers.easyslip.configured) {
      warnings.push(
        "PromptPay QR ต้องใช้ EasySlip API Key แต่ยังไม่ได้ตั้งค่าผู้ให้บริการ",
      );
    }
  }
  if (settings.bankTransfer.enabled) {
    if (activeBankAccounts(data.bankAccounts).length === 0) {
      warnings.push(
        "ช่องทางโอนผ่านบัญชีเปิดใช้งานอยู่แต่ยังไม่มีบัญชีธนาคารที่เปิดใช้งาน",
      );
    }
  }
  if (
    (settings.promptpay.enabled || settings.bankTransfer.enabled) &&
    !selectedProvider.configured
  ) {
    warnings.push(
      `${providerLabel(settings.slipProvider)} ยังไม่ได้ตั้งค่า แต่ช่องทางที่ต้องตรวจสลิปเปิดใช้งานอยู่`,
    );
  }
  if (settings.coupon.enabled && !settings.coupon.message.trim()) {
    warnings.push(
      "คูปองเปิดใช้งานอยู่แต่ข้อความคูปองว่าง ระบบจะใช้ข้อความเริ่มต้น",
    );
  }
  return warnings;
}

export function providerLastTestSummary(
  status: ProviderAdminStatus,
): string {
  if (status.lastTestAt === null) return "ยังไม่เคยทดสอบการเชื่อมต่อ";
  const tone = status.lastTestOk ? "สำเร็จ" : "ไม่สำเร็จ";
  const message = status.lastTestMessage ? ` — ${status.lastTestMessage}` : "";
  return `ทดสอบล่าสุด${tone}เมื่อ ${formatAdminDateTime(status.lastTestAt)}${message}`;
}
