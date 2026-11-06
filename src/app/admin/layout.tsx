import { Suspense } from "react";
import { requireAdminSession } from "@/lib/session";
import { AdminShell } from "@/components/admin/admin-shell";

export const metadata = { title: "ผู้ดูแลระบบ | KamenRaiderPlay", robots: { index: false, follow: false } };
export const instant = false;
async function ProtectedAdmin({ children }: { children: React.ReactNode }) {
  const { user } = await requireAdminSession();
  return <AdminShell user={{ id: user.id, name: user.name, email: user.email, image: user.image ?? null, role: user.role, balanceSatang: user.balanceSatang }}>{children}</AdminShell>;
}
export default function AdminLayout({ children }: { children: React.ReactNode }) {
  return <Suspense fallback={<p role="status" className="p-6">กำลังตรวจสอบสิทธิ์ผู้ดูแล…</p>}><ProtectedAdmin>{children}</ProtectedAdmin></Suspense>;
}
