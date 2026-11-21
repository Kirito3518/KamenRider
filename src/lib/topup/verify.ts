/**
 * Server-side slip checks. Nothing here trusts the client: the expected amount
 * and receiver come from the transaction snapshot written at creation time.
 *
 * The receiver matching follows the same strict rule as the reference
 * implementation (`0jay-v2/apps/web/src/server/services/topup/validation.ts`):
 * - a known receiving bank/code that differs is a veto;
 * - a known receiver name that differs is a veto, but a matching name alone is
 *   never enough;
 * - at least one account identifier (PromptPay proxy or bank account number)
 *   must positively match, tolerating provider masks.
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

// ---------------------------------------------------------------------------
// Strict comparisons ported from the reference implementation
// ---------------------------------------------------------------------------

export type CompareOutcome = "match" | "mismatch" | "insufficient";

/** Minimum number of visible characters required to trust a masked value. */
const MIN_VISIBLE_CHARS = 4;

const MASK_CHARS = /[xX*•]/;

function maskShape(value: string): string {
  const shape = value
    .toLowerCase()
    .replace(/\*/g, "x")
    .replace(/•/g, "x")
    .replace(/[^0-9x]/g, "");
  return /^66[0-9x]{9}$/.test(shape) ? `0${shape.slice(2)}` : shape;
}

/**
 * Compares an authoritative full value against a possibly-masked one.
 * A mask is a positional template: `x`/`*`/`•` = one digit, separators =
 * optional digits. At least {@link MIN_VISIBLE_CHARS} digits must be visible.
 */
export function compareMaskedValue(
  full: string | null | undefined,
  masked: string | null | undefined,
): CompareOutcome {
  const expected = (full ?? "").replace(/\D+/g, "");
  const text = (masked ?? "").trim();
  const actual = text.replace(/\D+/g, "");
  if (!expected || !actual) return "insufficient";
  if (!MASK_CHARS.test(text)) return actual === expected ? "match" : "mismatch";

  const visibleChars = (text.match(/\d/g) ?? []).length;
  if (visibleChars < MIN_VISIBLE_CHARS) return "insufficient";

  const pattern = [...text]
    .map((char) => {
      if (MASK_CHARS.test(char)) return "\\d";
      if (/\d/.test(char)) return char;
      return "\\d?";
    })
    .join("");
  return new RegExp(`^${pattern}$`).test(expected) ? "match" : "mismatch";
}

/**
 * Compares two values either of which may be masked. Neither masked: strict
 * equality. One masked: positional template against the other. Both masked:
 * same-shape masks must agree on every visible position and overlap at least
 * {@link MIN_VISIBLE_CHARS}; different mask styles only match when a visible
 * digit run of one appears in the other, otherwise "insufficient".
 */
export function compareMaskedPair(
  a: string | null | undefined,
  b: string | null | undefined,
): CompareOutcome {
  const left = maskShape(a ?? "");
  const right = maskShape(b ?? "");
  if (!left || !right) return "insufficient";
  const leftMasked = left.includes("x");
  const rightMasked = right.includes("x");
  if (!leftMasked && !rightMasked) return left === right ? "match" : "mismatch";
  if (!leftMasked) return compareMaskedValue(left, right);
  if (!rightMasked) return compareMaskedValue(right, left);

  if (left.length === right.length) {
    let overlap = 0;
    for (let index = 0; index < left.length; index += 1) {
      const l = left[index]!;
      const r = right[index]!;
      if (l === "x" || r === "x") continue;
      if (l !== r) return "mismatch";
      overlap += 1;
    }
    return overlap >= MIN_VISIBLE_CHARS ? "match" : "insufficient";
  }

  const longestRun = (value: string) =>
    (value.match(/\d+/g) ?? []).reduce(
      (best, run) => (run.length > best.length ? run : best),
      "",
    );
  const leftRun = longestRun(left);
  const rightRun = longestRun(right);
  if (leftRun.length < MIN_VISIBLE_CHARS || rightRun.length < MIN_VISIBLE_CHARS)
    return "insufficient";
  return leftRun.includes(rightRun) || rightRun.includes(leftRun)
    ? "match"
    : "insufficient";
}

const HONORIFIC_TOKENS = new Set([
  "นาย",
  "นาง",
  "นางสาว",
  "น.ส.",
  "mr",
  "mrs",
  "ms",
  "mr.",
  "mrs.",
  "ms.",
  "miss",
]);

function normalizeName(value: string): string {
  const collapsed = value.normalize("NFKC").trim().replace(/\s+/g, " ");
  const tokens = collapsed
    .split(" ")
    .map((token) => token.trim())
    .filter((token) => token && !HONORIFIC_TOKENS.has(token.toLowerCase()));
  if (tokens.length > 0) {
    tokens[0] = tokens[0].replace(/^(?:นางสาว|น\.ส\.|นาย|นาง)(?=\p{L})/u, "");
  }
  return tokens.filter(Boolean).join(" ").toLowerCase();
}

/**
 * Conservative receiver-name matching. Exact match after normalization wins;
 * otherwise every provider token must appear in order in the configured name,
 * allowing an abbreviated final token (providers cut surnames to a letter).
 */
export function compareReceiverName(
  expectedName: string,
  providerNames: Array<string | null | undefined>,
): CompareOutcome {
  const expected = normalizeName(expectedName);
  const candidates = providerNames
    .map((name) => normalizeName(name ?? ""))
    .filter(Boolean);
  if (!expected || candidates.length === 0) return "insufficient";

  const expectedTokens = expected.split(" ");
  for (const candidate of candidates) {
    if (candidate === expected) return "match";
    const candidateTokens = candidate.split(" ");
    const canAbbreviateLast =
      candidateTokens.length >= 2 && candidateTokens.at(-1)!.length <= 3;
    let cursor = 0;
    let allFound = true;
    for (const [index, token] of candidateTokens.entries()) {
      const isLast = index === candidateTokens.length - 1;
      let found = -1;
      for (let i = cursor; i < expectedTokens.length; i += 1) {
        const matches =
          expectedTokens[i] === token ||
          (isLast && canAbbreviateLast && expectedTokens[i].startsWith(token));
        if (matches) {
          found = i;
          break;
        }
      }
      if (found < 0) {
        allFound = false;
        break;
      }
      cursor = found + 1;
    }
    if (allFound) return "match";
  }
  return "mismatch";
}

/** Bank codes may arrive as "004", "4", or with separators; compare digits. */
export function compareBankCode(
  expected: string | null | undefined,
  actual: string | null | undefined,
): CompareOutcome {
  const normalize = (value: string | null | undefined) => {
    const digits = (value ?? "").replace(/\D+/g, "");
    return digits ? digits.replace(/^0+/, "") || "0" : "";
  };
  const left = normalize(expected);
  const right = normalize(actual);
  if (!left || !right) return "insufficient";
  return left === right ? "match" : "mismatch";
}

// ---------------------------------------------------------------------------
// Whole-recipient checks
// ---------------------------------------------------------------------------

export type RecipientCheck = {
  ok: boolean;
  outcome: CompareOutcome;
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
  // A known receiving bank that differs is decisive: that transfer could not
  // have reached the configured account.
  const bank = compareBankCode(row.bank_code, slip.receiverBankCode);
  if (bank === "mismatch")
    return {
      ok: false,
      outcome: "mismatch",
      code: "bank_mismatch",
      message: "ธนาคารผู้รับในสลิปไม่ตรงกับบัญชีที่ตั้งไว้",
    };

  // A known receiver name that differs is a veto, but a matching name alone is
  // never enough (masked surnames can look alike).
  const name = compareReceiverName(row.receiver_account_name ?? "", [
    slip.receiverName,
  ]);
  if (name === "mismatch")
    return {
      ok: false,
      outcome: "mismatch",
      code: "name_mismatch",
      message: "ชื่อบัญชีผู้รับในสลิปไม่ตรงกับบัญชีที่ตั้งไว้",
    };

  const account: CompareOutcome[] = [];
  if (row.channel === "promptpay") {
    if (row.promptpay_number?.trim() && slip.receiverProxyValue?.trim())
      account.push(
        compareMaskedPair(row.promptpay_number, slip.receiverProxyValue),
      );
    if (
      row.receiver_account_number?.trim() &&
      slip.receiverAccountNumber?.trim()
    )
      account.push(
        compareMaskedPair(
          row.receiver_account_number,
          slip.receiverAccountNumber,
        ),
      );
  } else if (
    row.receiver_account_number?.trim() &&
    slip.receiverAccountNumber?.trim()
  ) {
    account.push(
      compareMaskedPair(row.receiver_account_number, slip.receiverAccountNumber),
    );
  }

  if (account.includes("mismatch"))
    return {
      ok: false,
      outcome: "mismatch",
      code: "account_mismatch",
      message: "เลขบัญชีผู้รับในสลิปไม่ตรงกับบัญชีที่ตั้งไว้",
    };
  if (!account.includes("match"))
    return {
      ok: false,
      outcome: "insufficient",
      code: "recipient_unverified",
      message: "ไม่สามารถยืนยันบัญชีผู้รับในสลิปได้",
    };
  return {
    ok: true,
    outcome: "match",
    code: "recipient_matched",
    message: "บัญชีผู้รับตรงกัน",
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
