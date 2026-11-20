// @vitest-environment node
import { existsSync } from "node:fs";
import { join } from "node:path";
import { describe, expect, it } from "vitest";
import {
  BANK_PRESETS,
  getBankPreset,
  getBankPresetByLogoKey,
  isKnownBankLogoKey,
} from "@/lib/topup/banks";

const EXPECTED = [
  ["bbl", "002", "ธนาคารกรุงเทพ", "#1e4598"],
  ["kbank", "004", "ธนาคารกสิกรไทย", "#138f2d"],
  ["ktb", "006", "ธนาคารกรุงไทย", "#1ba5e1"],
  ["tmb", "011", "ธนาคารทหารไทยธนชาต", "#1279be"],
  ["scb", "014", "ธนาคารไทยพาณิชย์", "#4e2e7f"],
  ["bay", "025", "ธนาคารกรุงศรีอยุธยา", "#fec43b"],
  ["gsb", "030", "ธนาคารออมสิน", "#eb198d"],
  ["kk", "069", "ธนาคารเกียรตินาคินภัทร", "#199cc5"],
];

describe("bank presets", () => {
  it("matches the approved Admin Select list exactly", () => {
    expect(
      BANK_PRESETS.map((bank) => [bank.key, bank.code, bank.name, bank.color]),
    ).toEqual(EXPECTED);
  });

  it("resolves by code and logo key and has a bundled logo for every option", () => {
    expect(getBankPreset("004")?.key).toBe("kbank");
    expect(getBankPresetByLogoKey("kk")?.code).toBe("069");
    expect(isKnownBankLogoKey("tmb")).toBe(true);
    expect(isKnownBankLogoKey("unknown")).toBe(false);
    for (const bank of BANK_PRESETS) {
      expect(existsSync(join(process.cwd(), "public", bank.logoUrl))).toBe(true);
    }
  });
});
