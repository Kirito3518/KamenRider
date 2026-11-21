// @vitest-environment node
import { describe, expect, it } from "vitest";
import {
  checkAmount,
  checkRecipient,
  checkSlipDate,
  compareBankCode,
  compareMaskedPair,
  compareMaskedValue,
  maskedNumbersMatch,
  namesMatch,
} from "@/lib/topup/verify";
import type { NormalizedSlip } from "@/lib/topup/providers/types";
import type { TopupRow } from "@/lib/topup/transactions";

function slip(overrides: Partial<NormalizedSlip> = {}): NormalizedSlip {
  return {
    provider: "easyslip",
    transRef: "REF",
    amountSatang: 10_000,
    dateIso: new Date().toISOString(),
    senderName: "นาย ผู้โอน",
    receiverName: "ร้าน ทดสอบ",
    receiverBankCode: "004",
    receiverBankName: "กสิกรไทย",
    receiverAccountNumber: "1234567890",
    receiverProxyType: "MSISDN",
    receiverProxyValue: "0812345678",
    ...overrides,
  };
}

function row(overrides: Partial<TopupRow> = {}): TopupRow {
  return {
    channel: "promptpay",
    bank_code: "004",
    receiver_account_number: "1234567890",
    receiver_account_name: "ร้าน ทดสอบ",
    promptpay_type: "msisdn",
    promptpay_number: "0812345678",
    created_at: Date.now(),
    ...overrides,
  } as TopupRow;
}

describe("slip matching", () => {
  it("tolerates Thai titles and masked provider names", () => {
    expect(namesMatch("นาย ทดสอบ ใจดี", "ทดสอบ ใจดี")).toBe(true);
    expect(namesMatch("ร้าน ทดสอบ", "ร้านทดสอบ")).toBe(true);
    expect(namesMatch("ร้าน ทดสอบ", "ร้าน อื่น")).toBe(false);
  });

  it("compares masked account/proxy numbers by visible digits", () => {
    expect(maskedNumbersMatch("0812345678", "0812345678")).toBe(true);
    expect(maskedNumbersMatch("0812345678", "081xxx5678")).toBe(true);
    expect(maskedNumbersMatch("1234567890", "xxx-x-x7890-x")).toBe(true);
    expect(maskedNumbersMatch("0812345678", "081xxx9999")).toBe(false);
    expect(maskedNumbersMatch("0812345678", "xxx")).toBe(false);
  });

  it("accepts matching PromptPay proxy and amount", () => {
    expect(checkAmount(10_000, 10_000).ok).toBe(true);
    expect(checkAmount(10_000, 9_999).ok).toBe(false);
    const result = checkRecipient(row(), slip());
    expect(result.ok).toBe(true);
  });

  it("accepts bank accounts when bank code and masked account match", () => {
    const result = checkRecipient(
      row({ channel: "bank_transfer" }),
      slip({
        receiverAccountNumber: "xxx-xxx-7890",
        receiverProxyValue: null,
        receiverProxyType: null,
      }),
    );
    expect(result.ok).toBe(true);
  });

  it("rejects a bank transfer to another account", () => {
    const result = checkRecipient(
      row({ channel: "bank_transfer", receiver_account_number: "1234567890" }),
      slip({
        receiverName: "ร้าน อื่น",
        receiverAccountNumber: "1111111111",
        receiverBankCode: "014",
      }),
    );
    expect(result.ok).toBe(false);
  });

  it("rejects a known different bank even when account and name agree", () => {
    const result = checkRecipient(
      row({ channel: "bank_transfer" }),
      slip({ receiverBankCode: "014" }),
    );
    expect(result.ok).toBe(false);
    expect(result.code).toBe("bank_mismatch");
  });

  it("rejects a different receiver name even when the account matches", () => {
    const result = checkRecipient(
      row({ channel: "bank_transfer" }),
      slip({ receiverName: "ร้าน อื่น" }),
    );
    expect(result.ok).toBe(false);
    expect(result.code).toBe("name_mismatch");
  });

  it("never accepts a matching name alone without an account identifier", () => {
    const result = checkRecipient(
      row({ channel: "bank_transfer" }),
      slip({ receiverAccountNumber: null, receiverProxyValue: null }),
    );
    expect(result.ok).toBe(false);
    expect(result.code).toBe("recipient_unverified");
  });

  it("checks PromptPay bank, account and proxy together", () => {
    expect(checkRecipient(row(), slip({ receiverBankCode: "014" })).code).toBe(
      "bank_mismatch",
    );
    expect(
      checkRecipient(
        row(),
        slip({
          receiverProxyValue: "0899999999",
          receiverAccountNumber: "1111111111",
        }),
      ).code,
    ).toBe("account_mismatch");
  });

  it("normalizes bank codes and compares masked identifiers strictly", () => {
    expect(compareBankCode("004", "4")).toBe("match");
    expect(compareBankCode("004", "014")).toBe("mismatch");
    expect(compareBankCode(null, "014")).toBe("insufficient");
    expect(compareMaskedValue("1234567890", "xxx-xxx-7890")).toBe("match");
    expect(compareMaskedValue("1234567890", "xxx-xxx-9999")).toBe("mismatch");
    // Provider mask style that masks the final digit: 7890 is the visible run.
    expect(compareMaskedValue("1234078905", "xxx-x-x7890-x")).toBe("match");
    expect(compareMaskedValue("1234567890", "xxx-x-x7890-x")).toBe("mismatch");
    expect(compareMaskedPair("0812345678", "081xxx5678")).toBe("match");
    expect(compareMaskedPair("0812345678", "081xxx9999")).toBe("mismatch");
  });

  it("rejects stale slips and slips far in the future", () => {
    const createdAt = Date.now();
    expect(
      checkSlipDate({ created_at: createdAt }, new Date(createdAt - 60 * 60_000).toISOString()).ok,
    ).toBe(false);
    expect(
      checkSlipDate({ created_at: createdAt }, new Date(createdAt + 60 * 60_000).toISOString()).ok,
    ).toBe(false);
    expect(checkSlipDate({ created_at: createdAt }, null).ok).toBe(true);
  });
});
