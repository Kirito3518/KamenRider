import Link from "next/link";
import { Suspense } from "react";
import { LayoutDashboard, ShieldCheck, Users, Wallet } from "lucide-react";
import {
  AdminCard,
  AdminPage,
  AdminPageHeader,
  AdminStatCard,
  AdminStatGrid,
} from "@/components/admin/admin-page";
import { MemberTable } from "@/components/admin/member-table";
import { Button } from "@/components/ui/button";
import { CardAction, CardContent, CardDescription, CardHeader, CardTitle } from "@/components/ui/card";
import { getAdminData } from "@/lib/admin";
import { formatBalance } from "@/lib/money";

async function AdminOverview() {
  const { summary, members } = await getAdminData();
  return (
    <AdminPage>
      <AdminPageHeader
        icon={LayoutDashboard}
        title="ภาพรวมระบบ"
        description="ข้อมูลบัญชีและยอดเงินปัจจุบันของสมาชิก"
      />
      <AdminStatGrid className="sm:grid-cols-3 xl:grid-cols-3">
        <AdminStatCard
          label="สมาชิกทั้งหมด"
          value={summary.members.toLocaleString("th-TH")}
          hint="บัญชีในระบบ"
          icon={Users}
        />
        <AdminStatCard
          label="ผู้ดูแลระบบ"
          value={summary.admins.toLocaleString("th-TH")}
          hint="บัญชีที่มีสิทธิ์ admin"
          icon={ShieldCheck}
        />
        <AdminStatCard
          label="ยอดเงินสมาชิกรวม"
          value={formatBalance(summary.totalBalanceSatang)}
          hint="ยอดคงเหลือรวมทุกบัญชี"
          icon={Wallet}
        />
      </AdminStatGrid>
      <AdminCard>
        <CardHeader>
          <CardTitle>สมาชิกที่สมัครล่าสุด</CardTitle>
          <CardDescription>แสดง 5 บัญชีล่าสุด</CardDescription>
          <CardAction>
            <Button variant="outline" size="sm" nativeButton={false} render={<Link href="/admin/users" />}>
              ดูสมาชิก
            </Button>
          </CardAction>
        </CardHeader>
        <CardContent>
          <MemberTable members={members.slice(0, 5)} />
        </CardContent>
      </AdminCard>
    </AdminPage>
  );
}

export default function OverviewPage() {
  return (
    <Suspense fallback={<p role="status">กำลังโหลดภาพรวมระบบ…</p>}>
      <AdminOverview />
    </Suspense>
  );
}
