/**
 * Thai bank presets used by the Admin bank-account form and the customer bank
 * transfer screen. Logos are bundled under `public/topup/bank/<key>.svg`.
 * Bank codes are the same values returned by EasySlip/SlipOk, so receiver
 * matching can rely on them.
 */

export type BankPreset = {
  key: string;
  code: string;
  name: string;
  color: string;
  logoUrl: string;
};

const BANKS = [
  { key: "bbl", code: "002", name: "ธนาคารกรุงเทพ", color: "#1e4598" },
  { key: "kbank", code: "004", name: "ธนาคารกสิกรไทย", color: "#138f2d" },
  { key: "ktb", code: "006", name: "ธนาคารกรุงไทย", color: "#1ba5e1" },
  { key: "tmb", code: "011", name: "ธนาคารทหารไทยธนชาต", color: "#1279be" },
  { key: "scb", code: "014", name: "ธนาคารไทยพาณิชย์", color: "#4e2e7f" },
  { key: "bay", code: "025", name: "ธนาคารกรุงศรีอยุธยา", color: "#fec43b" },
  { key: "gsb", code: "030", name: "ธนาคารออมสิน", color: "#eb198d" },
  { key: "kk", code: "069", name: "ธนาคารเกียรตินาคินภัทร", color: "#199cc5" },
] as const;

export const BANK_PRESETS: readonly BankPreset[] = BANKS.map((bank) => ({
  ...bank,
  logoUrl: `/topup/bank/${bank.key}.svg`,
}));

const BY_CODE = new Map(BANK_PRESETS.map((bank) => [bank.code, bank]));
const BY_KEY = new Map(BANK_PRESETS.map((bank) => [bank.key, bank]));

export function getBankPreset(code: string): BankPreset | undefined {
  return BY_CODE.get(code);
}

export function getBankPresetByLogoKey(
  key: string | null | undefined,
): BankPreset | undefined {
  return key ? BY_KEY.get(key) : undefined;
}

export function isKnownBankLogoKey(key: string): boolean {
  return BY_KEY.has(key);
}
