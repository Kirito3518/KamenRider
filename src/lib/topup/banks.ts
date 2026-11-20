/**
 * Thai bank presets for the admin bank-account form. The list mirrors the bank
 * codes returned by EasySlip/SlipOk so receiver matching uses the same codes.
 * Preset logos are generated tiles (short name + bank colour); admins may upload
 * an official logo instead.
 */

export type BankPreset = {
  code: string;
  name: string;
  shortName: string;
  logoKey: string;
  color: string;
  foreground: string;
};

export const BANK_PRESETS: readonly BankPreset[] = [
  { code: "002", name: "ธนาคารกรุงเทพ", shortName: "BBL", logoKey: "bbl", color: "#1E4598", foreground: "#FFFFFF" },
  { code: "004", name: "ธนาคารกสิกรไทย", shortName: "KBANK", logoKey: "kbank", color: "#138F2D", foreground: "#FFFFFF" },
  { code: "006", name: "ธนาคารกรุงไทย", shortName: "KTB", logoKey: "ktb", color: "#1B75BC", foreground: "#FFFFFF" },
  { code: "011", name: "ธนาคารทหารไทยธนชาต", shortName: "TTB", logoKey: "ttb", color: "#F58220", foreground: "#FFFFFF" },
  { code: "014", name: "ธนาคารไทยพาณิชย์", shortName: "SCB", logoKey: "scb", color: "#4E2A84", foreground: "#FFFFFF" },
  { code: "017", name: "ธนาคารซิตี้แบงก์", shortName: "CITI", logoKey: "citi", color: "#003B70", foreground: "#FFFFFF" },
  { code: "020", name: "ธนาคารสแตนดาร์ดชาร์เตอร์ด", shortName: "SCBT", logoKey: "scbt", color: "#0072AA", foreground: "#FFFFFF" },
  { code: "022", name: "ธนาคารซีไอเอ็มบีไทย", shortName: "CIMBT", logoKey: "cimbt", color: "#7A0F2E", foreground: "#FFFFFF" },
  { code: "024", name: "ธนาคารยูโอบี", shortName: "UOB", logoKey: "uob", color: "#E31E24", foreground: "#FFFFFF" },
  { code: "025", name: "ธนาคารกรุงศรีอยุธยา", shortName: "BAY", logoKey: "bay", color: "#FDB913", foreground: "#1F2937" },
  { code: "030", name: "ธนาคารออมสิน", shortName: "GSB", logoKey: "gsb", color: "#EB198D", foreground: "#FFFFFF" },
  { code: "033", name: "ธนาคารอาคารสงเคราะห์", shortName: "GHB", logoKey: "ghb", color: "#FF6B00", foreground: "#FFFFFF" },
  { code: "034", name: "ธนาคารเพื่อการเกษตรและสหกรณ์การเกษตร", shortName: "BAAC", logoKey: "baac", color: "#3A9E3F", foreground: "#FFFFFF" },
  { code: "039", name: "ธนาคารมิซูโฮ", shortName: "MHCB", logoKey: "mhcb", color: "#D71920", foreground: "#FFFFFF" },
  { code: "045", name: "ธนาคารบีเอ็นพี พารีบาส์", shortName: "BNPP", logoKey: "bnpp", color: "#00915A", foreground: "#FFFFFF" },
  { code: "052", name: "ธนาคารแห่งประเทศจีน (ไทย)", shortName: "BOC", logoKey: "boc", color: "#B01F24", foreground: "#FFFFFF" },
  { code: "065", name: "ธนาคารธนชาต", shortName: "TBANK", logoKey: "tbank", color: "#00539B", foreground: "#FFFFFF" },
  { code: "066", name: "ธนาคารอิสลามแห่งประเทศไทย", shortName: "ISBT", logoKey: "isbt", color: "#0B6E4F", foreground: "#FFFFFF" },
  { code: "067", name: "ธนาคารทิสโก้", shortName: "TISCO", logoKey: "tisco", color: "#004B8D", foreground: "#FFFFFF" },
  { code: "069", name: "ธนาคารเกียรตินาคินภัทร", shortName: "KKP", logoKey: "kkp", color: "#0B2C6F", foreground: "#FFFFFF" },
  { code: "070", name: "ธนาคารไอซีบีซี (ไทย)", shortName: "ICBCT", logoKey: "icbct", color: "#C8102E", foreground: "#FFFFFF" },
  { code: "071", name: "ธนาคารไทยเครดิต", shortName: "TCD", logoKey: "tcd", color: "#00A651", foreground: "#FFFFFF" },
  { code: "073", name: "ธนาคารแลนด์ แอนด์ เฮ้าส์", shortName: "LH", logoKey: "lh", color: "#6A2C91", foreground: "#FFFFFF" },
  { code: "088", name: "ธนาคารคลิกซ์", shortName: "CLICX", logoKey: "clicx", color: "#00B5E2", foreground: "#FFFFFF" },
] as const;

const BY_CODE = new Map(BANK_PRESETS.map((bank) => [bank.code, bank]));
const BY_LOGO = new Map(BANK_PRESETS.map((bank) => [bank.logoKey, bank]));

export function getBankPreset(code: string): BankPreset | undefined {
  return BY_CODE.get(code);
}

export function getBankPresetByLogoKey(key: string | null | undefined): BankPreset | undefined {
  return key ? BY_LOGO.get(key) : undefined;
}

export function isKnownBankLogoKey(key: string): boolean {
  return BY_LOGO.has(key);
}
