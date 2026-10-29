import { getAuth } from "@/lib/auth";
import { requireAdminSession } from "@/lib/session";

export type AdminMember = { id: string; name: string; email: string; image: string | null; role: string; balanceSatang: number };
export async function getAdminData() {
  await requireAdminSession();
  const database = (await getAuth()).options.database;
  const summary = database.prepare("SELECT count(*) AS members, coalesce(sum(CASE WHEN role = 'admin' THEN 1 ELSE 0 END), 0) AS admins, coalesce(sum(balanceSatang), 0) AS totalBalanceSatang FROM user").get() as { members: number; admins: number; totalBalanceSatang: number };
  const members = database.prepare("SELECT id, name, email, image, role, balanceSatang FROM user ORDER BY createdAt DESC, id DESC LIMIT 50").all() as AdminMember[];
  return { summary, members };
}
