import { getAdminData } from "@/lib/admin";
import { Suspense } from "react";
import { MemberTable } from "@/components/admin/member-table";
import { formatBalance } from "@/lib/money";
import { Card, CardContent, CardDescription, CardHeader, CardTitle } from "@/components/ui/card";

async function AdminBalances() {
  const { members, summary } = await getAdminData();
  return <><div><p className="text-sm text-muted-foreground">KamenRaiderPlay Admin</p><h1 className="mt-1 text-2xl font-semibold">ยอดเงินสมาชิก</h1><p className="mt-2 text-sm text-muted-foreground">ยอดคงเหลือรวม {formatBalance(summary.totalBalanceSatang)}</p></div><Card><CardHeader><CardTitle>ยอดเงินรายบัญชี</CardTitle><CardDescription>แสดงไม่เกิน 50 บัญชีล่าสุด · หน้านี้ใช้ตรวจสอบยอดเงิน ยังไม่มีการปรับยอด</CardDescription></CardHeader><CardContent><MemberTable members={members} balances /></CardContent></Card></>;
}

export default function AdminBalancesPage() {
  return <Suspense fallback={<p role="status">กำลังโหลดยอดเงินสมาชิก…</p>}><AdminBalances /></Suspense>;
}
