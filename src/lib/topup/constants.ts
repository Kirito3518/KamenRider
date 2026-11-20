/**
 * Top-up configuration constants and shared enums.
 *
 * These values are deliberately server/client safe (no Node.js imports) so the
 * same limits can be rendered in the UI and enforced by the API.
 */

export const TOPUP_MIN_SATANG = 2_000; // 20.00 THB
export const TOPUP_MAX_SATANG = 10_000_000; // 100,000.00 THB

/** PromptPay QR transaction lifetime. */
export const PROMPTPAY_QR_EXPIRY_MS = 15 * 60 * 1_000;
/** Bank-transfer transaction lifetime (slip can be uploaded later). */
export const BANK_TRANSFER_EXPIRY_MS = 24 * 60 * 60 * 1_000;
/** TrueMoney pending link lifetime while waiting for a safe retry. */
export const TRUEMONEY_PENDING_EXPIRY_MS = 30 * 60 * 1_000;

/** Hard timeout for every provider HTTP call. */
export const PROVIDER_TIMEOUT_MS = 20_000;
/** A verification attempt may be retried this many times per transaction. */
export const MAX_VERIFICATION_ATTEMPTS = 5;
/** A processing transaction abandoned for this long may be retried. */
export const STALE_PROCESSING_MS = 2 * 60 * 1_000;
/** Minimum gap between two user-triggered verification attempts. */
export const MIN_RETRY_INTERVAL_MS = 5_000;

export const MAX_SLIP_BYTES = 4 * 1024 * 1024;
export const MAX_BANK_LOGO_BYTES = 32 * 1024;
export const TOPUP_PAGE_SIZE = 20;
export const TOPUP_MAX_PAGE_SIZE = 100;

export const TOPUP_CREATE_RATE_LIMIT = { windowMs: 60_000, max: 5 } as const;
export const SLIP_VERIFY_RATE_LIMIT = { windowMs: 10 * 60_000, max: 5 } as const;
export const TOPUP_PENDING_LIMIT = 5;

export const TOPUP_CHANNELS = ["truemoney", "promptpay", "bank_transfer", "coupon"] as const;
export type TopupChannel = (typeof TOPUP_CHANNELS)[number];

export const TOPUP_STATUSES = ["pending", "processing", "completed", "failed", "expired"] as const;
export type TopupStatus = (typeof TOPUP_STATUSES)[number];

export const VERIFICATION_STATUSES = [
  "pending",
  "verified",
  "rejected",
  "duplicate",
  "error",
  "unknown",
] as const;
export type VerificationStatus = (typeof VERIFICATION_STATUSES)[number];

export const SLIP_PROVIDERS = ["easyslip", "slipok"] as const;
export type SlipProvider = (typeof SLIP_PROVIDERS)[number];

export const PROMPTPAY_TYPES = ["msisdn", "natId"] as const;
export type PromptpayType = (typeof PROMPTPAY_TYPES)[number];

export const BANK_RAILS = ["promptpay_qr", "bank_transfer"] as const;
export type BankRail = (typeof BANK_RAILS)[number];

export const TOPUP_SORT_OPTIONS = ["newest", "oldest", "amount_desc", "amount_asc"] as const;
export type TopupSort = (typeof TOPUP_SORT_OPTIONS)[number];

export const RENTAL_SORT_OPTIONS = ["newest", "oldest", "amount_desc", "amount_asc"] as const;
export type RentalSort = (typeof RENTAL_SORT_OPTIONS)[number];

/** Statuses that are still actionable for a customer. */
export const ACTIVE_TOPUP_STATUSES: TopupStatus[] = ["pending", "processing"];

export function isTopupChannel(value: unknown): value is TopupChannel {
  return typeof value === "string" && (TOPUP_CHANNELS as readonly string[]).includes(value);
}

export function isTopupStatus(value: unknown): value is TopupStatus {
  return typeof value === "string" && (TOPUP_STATUSES as readonly string[]).includes(value);
}

export function isSlipProvider(value: unknown): value is SlipProvider {
  return typeof value === "string" && (SLIP_PROVIDERS as readonly string[]).includes(value);
}

export function isPromptpayType(value: unknown): value is PromptpayType {
  return typeof value === "string" && (PROMPTPAY_TYPES as readonly string[]).includes(value);
}

export function isTopupSort(value: unknown): value is TopupSort {
  return typeof value === "string" && (TOPUP_SORT_OPTIONS as readonly string[]).includes(value);
}

export function isRentalSort(value: unknown): value is RentalSort {
  return typeof value === "string" && (RENTAL_SORT_OPTIONS as readonly string[]).includes(value);
}
