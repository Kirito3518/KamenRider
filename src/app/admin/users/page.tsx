import { getAdminData } from "@/lib/admin";
import { Suspense } from "react";
import { MemberTable } from "@/components/admin/member-table";
import { Card, CardContent, CardDescription, CardHeader, CardTitle } from "@/components/ui/card";

async function AdminUsers() {
  const { members, summary } = await getAdminData();
  return <><div><p className="text-sm text-muted-foreground">KamenRaiderPlay Admin</p><h1 className="mt-1 text-2xl font-semibold">สมาชิก</h1></div><Card><CardHeader><CardTitle>รายชื่อสมาชิก</CardTitle><CardDescription>ทั้งหมด {summary.members} บัญชี · แสดงไม่เกิน 50 บัญชีล่าสุด</CardDescription></CardHeader><CardContent><MemberTable members={members} /></CardContent></Card></>;
}

export default function AdminUsersPage() {
  return <Suspense fallback={<p role="status">กำลังโหลดสมาชิก…</p>}><AdminUsers /></Suspense>;
}
