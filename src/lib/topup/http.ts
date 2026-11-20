import { apiJsonError, noStoreJson } from "@/lib/api-guard";
import { TopupError } from "@/lib/topup/errors";

export function mapTopupError(error: unknown): Response | null {
  if (error instanceof TopupError) {
    const headers: Record<string, string> | undefined = error.retryAfterSeconds
      ? { "Retry-After": String(error.retryAfterSeconds) }
      : undefined;
    return noStoreJson(
      { error: error.message, code: error.code },
      error.status,
      headers,
    );
  }
  return null;
}

export function topupError(error: unknown): Response {
  return apiJsonError(error, mapTopupError);
}
