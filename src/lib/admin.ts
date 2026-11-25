import type Database from "better-sqlite3";
import { getAuth } from "@/lib/auth";
import { requireAdminSession } from "@/lib/session";
import { readAdminMembers } from "@/lib/admin-members-data";

export type AdminMember = { id: string; name: string; email: string; image: string | null; role: string; balanceSatang: number };

export type MemberTotals = {
  members: number;
  admins: number;
  totalBalanceSatang: number;
};

/**
 * All-time member totals. Extracted from `getAdminData` so the overview dashboard
 * can reuse the exact same aggregate without loading a member list.
 */
export function readMemberTotals(database: Database.Database): MemberTotals {
  const row = database
    .prepare(
      "SELECT count(*) AS members, coalesce(sum(CASE WHEN role = 'admin' THEN 1 ELSE 0 END), 0) AS admins, coalesce(sum(balanceSatang), 0) AS totalBalanceSatang FROM user",
    )
    .get() as MemberTotals;
  return {
    members: Number(row.members) || 0,
    admins: Number(row.admins) || 0,
    totalBalanceSatang: Number(row.totalBalanceSatang) || 0,
  };
}

export async function getAdminData() {
  await requireAdminSession();
  const database = (await getAuth()).options.database;
  const summary = readMemberTotals(database);
  const members = database.prepare("SELECT id, name, email, image, role, balanceSatang FROM user ORDER BY createdAt DESC, id DESC LIMIT 50").all() as AdminMember[];
  return { summary, members };
}

export async function getAdminMembers(filters: Record<string, string | undefined>) {
  const session = await requireAdminSession();
  return { ...readAdminMembers((await getAuth()).options.database, filters), actorId: session.user.id };
}
