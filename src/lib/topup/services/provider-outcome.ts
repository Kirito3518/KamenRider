import type { ProviderFailure, ProviderResult } from "@/lib/topup/providers/types";

export function providerFailure(
  result: ProviderResult<unknown>,
): ProviderFailure | null {
  return result.kind === "ok" ? null : result;
}

export function isAmbiguousFailure(result: ProviderResult<unknown>): boolean {
  const failure = providerFailure(result);
  if (!failure) return false;
  if (
    failure.kind === "timeout" ||
    failure.kind === "network" ||
    failure.kind === "invalid-response"
  ) {
    return true;
  }
  return failure.httpStatus === null || failure.httpStatus >= 500;
}

export function isRateLimited(result: ProviderResult<unknown>): boolean {
  const failure = providerFailure(result);
  if (!failure) return false;
  return (
    failure.httpStatus === 429 ||
    /RATE_LIMIT/i.test(failure.code) ||
    /RATE_LIMIT/i.test(failure.message)
  );
}

export function isConfigFailure(result: ProviderResult<unknown>): boolean {
  const failure = providerFailure(result);
  if (!failure) return false;
  if (failure.httpStatus === 401 || failure.httpStatus === 403) return true;
  return [
    "1001",
    "1002",
    "1003",
    "1004",
    "unauthorized",
    "forbidden",
    "application_expired",
    "quota_exceeded",
    "missing_api_key",
    "invalid_api_key",
  ].includes(failure.code.toLowerCase());
}

export function isDuplicateSlipFailure(result: ProviderResult<unknown>): boolean {
  const failure = providerFailure(result);
  if (!failure) return false;
  return (
    failure.code === "1012" ||
    /duplicate_slip/i.test(failure.code) ||
    /สลิปซ้ำ|สลิปนี้เคย/i.test(failure.message)
  );
}

export function isAmountMismatchFailure(result: ProviderResult<unknown>): boolean {
  const failure = providerFailure(result);
  if (!failure) return false;
  return failure.code === "1013" || /amount_not_match|ยอด.*ไม่ตรง/i.test(failure.message);
}

export function isRecipientMismatchFailure(
  result: ProviderResult<unknown>,
): boolean {
  const failure = providerFailure(result);
  if (!failure) return false;
  return (
    failure.code === "1014" ||
    /account_not_match|บัญชีผู้รับ.*ไม่ตรง/i.test(failure.message)
  );
}

export function isRetryableProviderPending(
  result: ProviderResult<unknown>,
): boolean {
  const failure = providerFailure(result);
  if (!failure) return false;
  return (
    failure.code === "slip_pending" ||
    failure.code === "1009" ||
    failure.code === "1010" ||
    failure.retryable
  );
}

export function isInvalidSlipFailure(result: ProviderResult<unknown>): boolean {
  const failure = providerFailure(result);
  if (!failure) return false;
  return [
    "1005",
    "1006",
    "1007",
    "1008",
    "1011",
    "invalid_image",
    "image_size_too_large",
    "qrcode_not_found",
    "slip_not_found",
    "invalid_payload",
    "unsupported_file",
  ].includes(failure.code) || /invalid_image|qrcode_not_found|slip_not_found/i.test(failure.code);
}

export function isSlipExpiredFailure(result: ProviderResult<unknown>): boolean {
  const failure = providerFailure(result);
  if (!failure) return false;
  return (
    failure.code === "slip_expired" ||
    failure.code === "slip_not_found" ||
    failure.code === "1011"
  );
}
