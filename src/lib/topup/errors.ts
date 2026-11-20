/** Shared, client-safe top-up error contract. */

export type TopupErrorCode =
  | "invalid-request"
  | "channel-disabled"
  | "amount-out-of-range"
  | "not-configured"
  | "idempotency-conflict"
  | "transaction-not-found"
  | "transaction-finished"
  | "verification-in-progress"
  | "too-many-attempts"
  | "file-too-large"
  | "unsupported-file"
  | "duplicate-slip"
  | "duplicate-voucher"
  | "amount-mismatch"
  | "recipient-mismatch"
  | "invalid-slip"
  | "slip-expired"
  | "provider-unavailable"
  | "provider-timeout"
  | "rate-limited"
  | "pending-review"
  | "account-not-found"
  | "settings-conflict"
  | "secret-unavailable"
  | "last-bank-account"
  | "internal-error";

export class TopupError extends Error {
  readonly code: TopupErrorCode;
  readonly status: number;
  readonly retryAfterSeconds?: number;

  constructor(
    code: TopupErrorCode,
    message: string,
    status = 400,
    retryAfterSeconds?: number,
  ) {
    super(message);
    this.name = "TopupError";
    this.code = code;
    this.status = status;
    this.retryAfterSeconds = retryAfterSeconds;
  }
}
