import Link from "next/link";
import { Suspense } from "react";
import { getAdminData } from "@/lib/admin";
import { formatBalance } from "@/lib/money";
import { Card, CardContent, CardDescription, CardHeader, CardTitle } from "@/components/ui/card";
import { Button } from "@/components/ui/button";
import { MemberTable } from "@/components/admin/member-table";

async function AdminOverview() {
  const { summary, members } = await getAdminData();
  return <><div><p className="text-sm text-muted-foreground">KamenRaiderPlay Admin</p><h1 className="mt-1 text-2xl font-semibold">ภาพรวมระบบ</h1><p className="mt-2 text-sm text-muted-foreground">ข้อมูลบัญชีและยอดเงินปัจจุบันของสมาชิก</p></div>
    <div className="grid gap-4 sm:grid-cols-3">{[
      ["สมาชิกทั้งหมด", summary.members.toLocaleString("th-TH"), "บัญชีในระบบ"],
      ["ผู้ดูแลระบบ", summary.admins.toLocaleString("th-TH"), "บัญชีที่มีสิทธิ์ admin"],
      ["ยอดเงินสมาชิกรวม", formatBalance(summary.totalBalanceSatang), "ยอดคงเหลือรวมทุกบัญชี"],
    ].map(([title, value, description]) => <Card key={title}><CardHeader><CardTitle>{title}</CardTitle><CardDescription>{description}</CardDescription></CardHeader><CardContent><p className="text-3xl font-semibold tabular-nums">{value}</p></CardContent></Card>)}</div>
    <Card><CardHeader><div className="flex items-center justify-between gap-4"><CardTitle>สมาชิกที่สมัครล่าสุด</CardTitle><Button variant="outline" size="sm" nativeButton={false} render={<Link href="/admin/users" />}>ดูสมาชิก</Button></div><CardDescription>แสดง 5 บัญชีล่าสุด</CardDescription></CardHeader><CardContent><MemberTable members={members.slice(0, 5)} /></CardContent></Card>
  </>;
}

export default function AdminPage() {
  return <Suspense fallback={<p role="status">กำลังโหลดภาพรวมระบบ…</p>}><AdminOverview /></Suspense>;
}
