/** Placeholder shown when the Agent has not reported a value (or the value is unusable). */
export const missingSpecLabel = "—";

/** Trimmed, non-empty Agent-reported hardware text, or a neutral placeholder. */
export function formatSpec(value: string | null | undefined) {
  const trimmed = typeof value === "string" ? value.trim() : "";
  return trimmed || missingSpecLabel;
}

const GIB = 1024 ** 3;
const MIB = 1024 ** 2;
const KIB = 1024;

/**
 * Formats an Agent-reported RAM byte count for display. Installed memory rarely
 * divides exactly into whole GiB, so the nearest whole GiB is used (e.g.
 * 34,292,871,168 bytes -> "32 GB").
 */
export function formatRamBytes(bytes: number | null | undefined) {
  if (typeof bytes !== "number" || !Number.isFinite(bytes) || bytes <= 0)
    return missingSpecLabel;
  if (bytes >= GIB) return `${Math.round(bytes / GIB)} GB`;
  if (bytes >= MIB) return `${Math.round(bytes / MIB)} MB`;
  return `${Math.round(bytes / KIB)} KB`;
}

/** RAM in whole GiB, for the legacy numeric `ram` field of the public machine list. */
export function ramGibibytes(bytes: number | null | undefined) {
  if (typeof bytes !== "number" || !Number.isFinite(bytes) || bytes <= 0) return 0;
  return Math.round(bytes / GIB);
}

/** Formats a UTC timestamp for local display; invalid/missing values become a placeholder. */
export function formatLocalDateTime(value: string | null | undefined) {
  if (!value) return missingSpecLabel;
  const parsed = Date.parse(value);
  return Number.isFinite(parsed)
    ? new Date(parsed).toLocaleString("th-TH")
    : missingSpecLabel;
}
