import type Database from "better-sqlite3";
import { z } from "zod";

const common = { requestId: z.uuid(), reason: z.string().trim().min(3).max(300) };
export const memberChangeSchema = z.discriminatedUnion("action", [
  z.object({ ...common, action: z.literal("credit"), amount: z.string() }),
  z.object({ ...common, action: z.literal("debit"), amount: z.string() }),
  z.object({ ...common, action: z.literal("role"), role: z.enum(["user", "admin"]) }),
]);
export class MemberChangeError extends Error {
  status: number;
  constructor(message: string, status = 400) { super(message); this.status = status; }
}

export function applyMemberChange(database: Database.Database, actorId: string, targetId: string, input: unknown) {
  const parsed = memberChangeSchema.safeParse(input);
  if (!parsed.success) throw new MemberChangeError("ข้อมูลไม่ถูกต้อง กรุณาระบุเหตุผลอย่างน้อย 3 ตัวอักษร");
  const data = parsed.data;
  let amount = 0;
  if (data.action !== "role") {
    if (!/^\d{1,10}(?:\.\d{1,2})?$/.test(data.amount)) throw new MemberChangeError("จำนวนเงินต้องเป็นบาทและมีทศนิยมไม่เกิน 2 ตำแหน่ง");
    const [whole, cents = ""] = data.amount.split(".");
    amount = Number(whole) * 100 + Number(cents.padEnd(2, "0"));
    if (!Number.isSafeInteger(amount) || amount <= 0) throw new MemberChangeError("จำนวนเงินต้องมากกว่า 0");
  }
  database.exec(`CREATE TABLE IF NOT EXISTS adminMemberAudit (
    requestId TEXT PRIMARY KEY, actorId TEXT NOT NULL, targetId TEXT NOT NULL,
    action TEXT NOT NULL, amountSatang INTEGER NOT NULL, reason TEXT NOT NULL,
    balanceBefore INTEGER NOT NULL, balanceAfter INTEGER NOT NULL,
    roleBefore TEXT NOT NULL, roleAfter TEXT NOT NULL, createdAt INTEGER NOT NULL
  ); CREATE INDEX IF NOT EXISTS adminMemberAudit_target ON adminMemberAudit(targetId, createdAt);`);
  return database.transaction(() => {
    const actor = database.prepare("SELECT role FROM user WHERE id = ?").get(actorId) as { role: string } | undefined;
    if (actor?.role !== "admin") throw new MemberChangeError("ไม่มีสิทธิ์จัดการสมาชิก", 403);
    const target = database.prepare("SELECT role, balanceSatang FROM user WHERE id = ?").get(targetId) as { role: string; balanceSatang: number } | undefined;
    if (!target) throw new MemberChangeError("ไม่พบบัญชีสมาชิก", 404);
    const repeated = database.prepare("SELECT * FROM adminMemberAudit WHERE requestId = ?").get(data.requestId) as { actorId: string; targetId: string; action: string; amountSatang: number; reason: string; roleAfter: string } | undefined;
    if (repeated) {
      if (repeated.actorId !== actorId || repeated.targetId !== targetId || repeated.action !== data.action || repeated.amountSatang !== amount || repeated.reason !== data.reason || (data.action === "role" && repeated.roleAfter !== data.role)) throw new MemberChangeError("รหัสคำขอซ้ำกับรายการอื่น กรุณาเริ่มรายการใหม่", 409);
      return target;
    }
    const nextRole = data.action === "role" ? data.role : target.role;
    if (data.action === "role" && actorId === targetId && nextRole !== "admin") throw new MemberChangeError("ไม่สามารถลดสิทธิ์ผู้ดูแลของตัวเองได้");
    if (target.role === "admin" && nextRole !== "admin") {
      const admins = database.prepare("SELECT count(*) AS count FROM user WHERE role = 'admin'").get() as { count: number };
      if (admins.count <= 1) throw new MemberChangeError("ต้องเหลือผู้ดูแลระบบอย่างน้อยหนึ่งบัญชี");
    }
    const nextBalance = target.balanceSatang + (data.action === "credit" ? amount : data.action === "debit" ? -amount : 0);
    if (!Number.isSafeInteger(nextBalance)) throw new MemberChangeError("ยอดเงินเกินขอบเขตที่ระบบรองรับ");
    if (nextBalance < 0) throw new MemberChangeError("ยอดเงินคงเหลือไม่เพียงพอ");
    const now = Date.now();
    database.prepare("UPDATE user SET balanceSatang = ?, role = ?, updatedAt = ? WHERE id = ?").run(nextBalance, nextRole, new Date(now).toISOString(), targetId);
    database.prepare("INSERT INTO adminMemberAudit VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?)").run(data.requestId, actorId, targetId, data.action, amount, data.reason, target.balanceSatang, nextBalance, target.role, nextRole, now);
    return { role: nextRole, balanceSatang: nextBalance };
  }).immediate();
}
