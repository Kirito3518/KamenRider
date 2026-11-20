/** Money helpers for top-ups. All amounts are integer satang. */

const BAHT_PATTERN = /^(\d{1,9})(?:\.(\d{1,2}))?$/;

/** Parses a human-entered baht amount without going through floating point. */
export function parseBahtToSatang(value: unknown): number | null {
  if (typeof value === "number") {
    if (!Number.isFinite(value) || value <= 0) return null;
    const satang = Math.round(value * 100);
    return Number.isSafeInteger(satang) && satang > 0 ? satang : null;
  }
  if (typeof value !== "string") return null;
  const text = value.trim().replace(/,/g, "");
  if (!BAHT_PATTERN.test(text)) return null;
  const [, whole, fraction = ""] = BAHT_PATTERN.exec(text)!;
  const satang = Number(whole) * 100 + Number(fraction.padEnd(2, "0"));
  return Number.isSafeInteger(satang) && satang > 0 ? satang : null;
}

/** Parses an amount returned by a provider (number or decimal string). */
export function parseProviderAmountToSatang(value: unknown): number | null {
  if (typeof value === "number") {
    if (!Number.isFinite(value) || value <= 0) return null;
    const satang = Math.round(value * 100);
    return Number.isSafeInteger(satang) && satang > 0 ? satang : null;
  }
  const parsed = parseBahtToSatang(value);
  if (parsed !== null) return parsed;
  if (typeof value === "string") {
    const text = value.trim().replace(/,/g, "");
    if (/^\d{1,9}\.\d{3,4}$/.test(text)) {
      const satang = Math.round(Number(text) * 100);
      return Number.isSafeInteger(satang) && satang > 0 ? satang : null;
    }
  }
  return null;
}

/** Provider-friendly baht number (2 decimal places) for an integer satang amount. */
export function satangToBahtNumber(satang: number): number {
  if (!Number.isSafeInteger(satang) || satang < 0) {
    throw new TypeError("Invalid satang amount");
  }
  return Number((satang / 100).toFixed(2));
}

/** Amount for display while editing, e.g. 10000 -> "100.00". */
export function satangToBahtInput(satang: number | null | undefined): string {
  if (satang === null || satang === undefined || !Number.isSafeInteger(satang)) return "";
  return (satang / 100).toFixed(2);
}

export function formatBahtNumber(satang: number): string {
  return (satang / 100).toLocaleString("th-TH", {
    minimumFractionDigits: satang % 100 === 0 ? 0 : 2,
    maximumFractionDigits: 2,
  });
}

/** Keeps the first 3 and last 4 digits visible; useful for phone/account hints. */
export function maskAccountNumber(value: string | null | undefined): string {
  if (!value) return "—";
  const text = value.replace(/\s+/g, "");
  if (text.length <= 7) return text;
  return `${text.slice(0, 3)}••••${text.slice(-4)}`;
}

export function maskPhone(value: string | null | undefined): string {
  if (!value) return "—";
  const digits = value.replace(/\D+/g, "");
  if (digits.length < 7) return maskAccountNumber(digits);
  return `${digits.slice(0, 3)}-•••-${digits.slice(-4)}`;
}

export function maskSecret(value: string | null | undefined): string {
  if (!value) return "";
  if (value.length <= 4) return "••••";
  return `••••••••${value.slice(-4)}`;
}

export function maskPromptpayNumber(
  type: "msisdn" | "natId",
  value: string | null | undefined,
): string {
  if (!value) return "—";
  const digits = value.replace(/\D+/g, "");
  if (type === "msisdn") return maskPhone(digits);
  if (digits.length <= 7) return digits;
  return `${digits.slice(0, 3)}•••••••••${digits.slice(-4)}`;
}

/** Formats a Thai bank account number in groups of 3-3-3-x, preserving unknown lengths. */
export function formatBankAccountNumber(value: string): string {
  const digits = value.replace(/\D+/g, "");
  if (!digits) return value;
  const groups = digits.match(/.{1,3}/g) ?? [digits];
  return groups.join("-");
}

export function isValidBankAccountNumber(value: string): boolean {
  const digits = value.replace(/[\s-]/g, "");
  return /^\d{8,15}$/.test(digits);
}

export function isValidPhoneNumber(value: string): boolean {
  return /^0\d{9}$/.test(value.replace(/[\s-]/g, ""));
}

export function isValidNatId(value: string): boolean {
  return /^\d{13}$/.test(value.replace(/[\s-]/g, ""));
}
