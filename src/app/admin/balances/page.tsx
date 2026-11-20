import { Suspense } from "react";
import { ShieldCheck, Users, Wallet } from "lucide-react";
import {
  AdminCard,
  AdminPage,
  AdminPageHeader,
  AdminStatCard,
  AdminStatGrid,
} from "@/components/admin/admin-page";
import { MemberTable } from "@/components/admin/member-table";
import { CardContent, CardDescription, CardHeader, CardTitle } from "@/components/ui/card";
import { getAdminData } from "@/lib/admin";
import { formatBalance } from "@/lib/money";

async function AdminBalances() {
  const { members, summary } = await getAdminData();
  return (
    <AdminPage>
      <AdminPageHeader
        icon={Wallet}
        title="ยอดเงินสมาชิก"
        description="ยอดคงเหลือปัจจุบันของสมาชิก · ระบบหักค่าเช่าและคืนเงินตามเวลาที่ไม่ได้ใช้ให้อัตโนมัติ"
      />
      <AdminStatGrid className="sm:grid-cols-3 xl:grid-cols-3">
        <AdminStatCard
          label="ยอดเงินรวมในระบบ"
          value={formatBalance(summary.totalBalanceSatang)}
          hint="ยอดคงเหลือรวมทุกบัญชี"
          icon={Wallet}
        />
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
      </AdminStatGrid>
      <AdminCard>
        <CardHeader>
          <CardTitle>ยอดเงินรายบัญชี</CardTitle>
          <CardDescription>แสดงไม่เกิน 50 บัญชีล่าสุด</CardDescription>
        </CardHeader>
        <CardContent>
          <MemberTable members={members} balances />
        </CardContent>
      </AdminCard>
    </AdminPage>
  );
}

export default function BalancesPage() {
  return (
    <Suspense fallback={<p role="status">กำลังโหลดยอดเงินสมาชิก…</p>}>
      <AdminBalances />
    </Suspense>
  );
}
