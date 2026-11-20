/**
 * Client-safe Parsec attempt contracts shared by the renter UI and API proxies.
 * The Controller owns the state machine; the browser only renders it.
 */

export type ParsecAttemptState =
  | "queued"
  | "starting"
  | "waiting_email"
  | "ready"
  | "failed";

export type ParsecAttempt = {
  attemptId: string;
  state: ParsecAttemptState;
  errorCode: string | null;
  message: string | null;
  createdAtUtc: string;
  updatedAtUtc: string;
  expiresAtUtc: string;
};

/** What the renter may do on the machine this lease is attached to. */
export type ParsecLeaseState = {
  available: boolean;
  bootKeyId: string | null;
  publicKey: string | null;
  attempt: ParsecAttempt | null;
};

export const PARSEC_STATE_LABELS: Record<ParsecAttemptState, string> = {
  queued: "กำลังส่งคำขอไปยังเครื่อง",
  starting: "กำลังเข้าสู่ระบบ Parsec บนเครื่อง",
  waiting_email: "Parsec ขอยืนยันอีเมล/อุปกรณ์ — โปรดยืนยันแล้วลองอีกครั้ง",
  ready: "เชื่อมต่อ Parsec สำเร็จ",
  failed: "เชื่อมต่อ Parsec ไม่สำเร็จ",
};

/**
 * Thai wording for every Agent error code (mirrors 0jay's parsec-connect dialog).
 * Unknown codes fall back to a generic message plus the code.
 */
export const PARSEC_ERROR_LABELS: Record<string, string> = {
  INVALID_CREDENTIALS: "อีเมลหรือรหัสผ่าน Parsec ไม่ถูกต้อง",
  CAPTCHA_REQUIRED: "Parsec ขอให้ยืนยันแคปชา — ต้องทำที่หน้าเครื่อง",
  APP_NOT_FOUND: "เครื่องนี้ยังไม่ได้ติดตั้ง Parsec",
  WINDOW_UNAVAILABLE: "เปิดหน้าต่าง Parsec บนเครื่องไม่ได้",
  HOST_NOT_READY: "ยังไม่มีผู้ใช้ Windows ล็อกอินอยู่ที่หน้าเครื่อง",
  TIMEOUT: "หมดเวลารอผลจากเครื่อง",
  AGENT_OFFLINE: "Agent ของเครื่องนี้ไม่ได้เชื่อมต่อ",
  RATE_LIMITED: "Parsec จำกัดจำนวนครั้ง — รอสักครู่แล้วลองใหม่",
  ACCOUNT_LOCKED: "บัญชี Parsec ถูกล็อก",
  SERVICE_UNAVAILABLE: "Parsec ปิดปรับปรุงอยู่",
  NETWORK_ERROR: "เครื่องเชื่อมต่อ Parsec ไม่ได้",
  SESSION_CONFLICT: "บัญชีนี้มี session Parsec ซ้อนอยู่",
  PASSWORD_RESET_REQUIRED: "Parsec บังคับให้ตั้งรหัสผ่านใหม่",
  APP_UPDATE_REQUIRED: "Parsec บนเครื่องต้องอัปเดตก่อน",
  TWO_FACTOR_UNSUPPORTED: "Parsec นี้ขอรหัส 2FA ซึ่งระบบยังไม่รองรับ",
  PARSEC_BUSY: "มีการเข้าสู่ระบบ Parsec อื่นกำลังทำงานอยู่",
  LOGIN_UNCONFIRMED: "ไม่พบหลักฐานว่าเข้าสู่ระบบ Parsec สำเร็จ",
  LEASE_ENDED: "การเช่าสิ้นสุดแล้ว",
  AGENT_REBOOTED: "เครื่องรีสตาร์ตระหว่างดำเนินการ",
  INTERNAL: "เกิดข้อผิดพลาดภายใน Agent",
};

export function parsecErrorLabel(code: string | null | undefined) {
  if (!code) return "เชื่อมต่อ Parsec ไม่สำเร็จ";
  return PARSEC_ERROR_LABELS[code] ?? `เชื่อมต่อ Parsec ไม่สำเร็จ (${code})`;
}

export function parsecAttemptIsRunning(attempt: ParsecAttempt | null) {
  return (
    attempt?.state === "queued" ||
    attempt?.state === "starting" ||
    attempt?.state === "waiting_email"
  );
}
