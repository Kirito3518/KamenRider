/**
 * Server-side slip checks. Nothing here trusts the client: the expected amount
 * and receiver come from the transaction snapshot written at creation time.
 */

import type { NormalizedSlip } from "@/lib/topup/providers/types";
import type { TopupRow } from "@/lib/topup/transactions";

const TITLES = [
  "นางสาว",
  "นาง",
  "นาย",
  "miss",
  "mrs",
  "mr",
  "ms",
  "dr",
  "ดร",
];

function stripTitles(value: string) {
  let text = value.trim().toLowerCase();
  for (const title of TITLES) {
    if (text.startsWith(title)) {
      text = text.slice(title.length).trim();
      break;
    }
  }
  return text;
}

/** Normalizes Thai/English person names for tolerant comparison. */
export function normalizePersonName(value: string | null | undefined): string {
  if (!value) return "";
  return stripTitles(value)
    .replace(/[\s.*_\-()[\]{}'"`/,:;]+/g, "")
    .replace(/[^\p{L}\p{N}]/gu, "");
}

function tokenizeName(value: string | null | undefined): string[] {
  if (!value) return [];
  return stripTitles(value)
    .replace(/[\p{P}\p{S}]+/gu, " ")
    .split(/\s+/)
    .map((token) => normalizePersonName(token))
    .filter((token) => token.length >= 2);
}

export function namesMatch(
  expected: string | null | undefined,
  actual: string | null | undefined,
): boolean {
  const left = normalizePersonName(expected);
  const right = normalizePersonName(actual);
  if (!left || !right) return false;
  if (left === right) return true;
  if (left.length >= 4 && right.includes(left)) return true;
  if (right.length >= 4 && left.includes(right)) return true;

  const leftTokens = tokenizeName(expected);
  const rightTokens = tokenizeName(actual);
  if (leftTokens.length === 0 || rightTokens.length === 0) return false;
  let matches = 0;
  for (const token of leftTokens) {
    if (
      rightTokens.some(
        (other) =>
          other === token ||
          (token.length >= 3 && other.includes(token)) ||
          (other.length >= 3 && token.includes(other)),
      )
    ) {
      matches += 1;
    }
  }
  return matches >= Math.min(2, leftTokens.length) && matches >= 1;
}

/** Compares full or provider-masked account/proxy numbers. */
export function maskedNumbersMatch(
  expected: string | null | undefined,
  actual: string | null | undefined,
): boolean {
  const expectedDigits = (expected ?? "").replace(/\D+/g, "");
  if (!actual || expectedDigits.length < 4) return false;
  const text = actual.trim();
  const actualDigits = text.replace(/\D+/g, "");
  const hasMask = /[xX*•]/.test(text);
  if (!hasMask) {
    if (actualDigits.length < 4) return false;
    if (actualDigits === expectedDigits) return true;
    return expectedDigits.endsWith(actualDigits.slice(-4));
  }

  const firstMask = text.search(/[xX*•]/);
  const lastMask = Math.max(
    text.lastIndexOf("x"),
    text.lastIndexOf("X"),
    text.lastIndexOf("*"),
    text.lastIndexOf("•"),
  );
  const prefix = text.slice(0, firstMask).replace(/\D+/g, "");
  const suffix = text.slice(lastMask + 1).replace(/\D+/g, "");
  if (prefix && suffix) {
    return (
      prefix.length + suffix.length >= 4 &&
      expectedDigits.startsWith(prefix) &&
      expectedDigits.endsWith(suffix)
    );
  }
  // Masked in the middle (e.g. xxx-x-x7890-x): compare the visible run to the
  // expected suffix; banks normally preserve the last four digits.
  const visibleRuns = text.match(/\d+/g) ?? [];
  const longest = visibleRuns
    .filter((run) => run.length >= 4)
    .sort((left, right) => right.length - left.length)[0];
  if (longest && expectedDigits.endsWith(longest)) return true;
  if (prefix.length >= 3 && expectedDigits.startsWith(prefix)) return true;
  if (suffix.length >= 3 && expectedDigits.endsWith(suffix)) return true;
  return false;
}

function codeMatches(
  expected: string | null | undefined,
  actual: string | null | undefined,
): boolean {
  if (!expected || !actual) return false;
  return expected.replace(/\D+/g, "") === actual.replace(/\D+/g, "");
}

function proxyTypeMatches(
  expected: "msisdn" | "natId" | null,
  actual: string | null,
): boolean {
  if (!actual) return false;
  const normalized = actual.trim().toUpperCase();
  if (expected === "msisdn") return normalized === "MSISDN";
  if (expected === "natId") return normalized === "NATID" || normalized === "NAT_ID";
  return false;
}

export type RecipientCheck = {
  ok: boolean;
  code: string;
  message: string;
};

export function checkRecipient(
  row: Pick<
    TopupRow,
    | "channel"
    | "bank_code"
    | "receiver_account_number"
    | "receiver_account_name"
    | "promptpay_type"
    | "promptpay_number"
  >,
  slip: NormalizedSlip,
): RecipientCheck {
  const expectedName = row.receiver_account_name;
  const nameMatch = namesMatch(expectedName, slip.receiverName);

  if (row.channel === "promptpay") {
    const proxyMatch =
      maskedNumbersMatch(row.promptpay_number, slip.receiverProxyValue) &&
      proxyTypeMatches(row.promptpay_type, slip.receiverProxyType);
    const accountMatch = maskedNumbersMatch(
      row.promptpay_number,
      slip.receiverAccountNumber,
    );
    if (proxyMatch || accountMatch) {
      return { ok: true, code: "recipient_matched", message: "บัญชีผู้รับตรงกัน" };
    }
    if (nameMatch) {
      return {
        ok: true,
        code: "recipient_name_matched",
        message: "ชื่อบัญชีผู้รับตรงกัน",
      };
    }
    return {
      ok: false,
      code: "recipient_unverified",
      message: "ไม่สามารถยืนยันบัญชีผู้รับในสลิปได้",
    };
  }

  const bankMatch = codeMatches(row.bank_code, slip.receiverBankCode);
  const accountMatch = maskedNumbersMatch(
    row.receiver_account_number,
    slip.receiverAccountNumber,
  );
  if ((bankMatch && (accountMatch || nameMatch)) || (accountMatch && nameMatch)) {
    return { ok: true, code: "recipient_matched", message: "บัญชีผู้รับตรงกัน" };
  }
  return {
    ok: false,
    code: "recipient_unverified",
    message: "บัญชีผู้รับในสลิปไม่ตรงกับรายการ",
  };
}

export function checkSlipDate(
  row: Pick<TopupRow, "created_at">,
  dateIso: string | null,
  now = Date.now(),
): { ok: boolean; message: string } {
  if (!dateIso) {
    return { ok: true, message: "ผู้ให้บริการไม่ได้ระบุเวลาสลิป" };
  }
  const parsed = Date.parse(dateIso);
  if (!Number.isFinite(parsed)) {
    return { ok: false, message: "เวลาบนสลิปไม่ถูกต้อง" };
  }
  const earliest = row.created_at - 10 * 60 * 1_000;
  const latest = now + 5 * 60 * 1_000;
  if (parsed < earliest) {
    return {
      ok: false,
      message: "สลิปนี้เก่ากว่ารายการเติมเงิน กรุณาใช้สลิปใหม่",
    };
  }
  if (parsed > latest) {
    return { ok: false, message: "เวลาบนสลิปไม่อยู่ในช่วงที่ถูกต้อง" };
  }
  return { ok: true, message: "เวลาสลิปถูกต้อง" };
}

export function checkAmount(
  expectedSatang: number | null,
  actualSatang: number | null,
): { ok: boolean; message: string } {
  if (expectedSatang === null) {
    return { ok: false, message: "รายการไม่มีจำนวนเงินที่ต้องตรวจสอบ" };
  }
  if (actualSatang === null) {
    return { ok: false, message: "ไม่พบยอดเงินในสลิป" };
  }
  if (actualSatang !== expectedSatang) {
    return { ok: false, message: "ยอดเงินในสลิปไม่ตรงกับรายการ" };
  }
  return { ok: true, message: "ยอดเงินตรงกัน" };
}
