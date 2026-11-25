import type { LucideIcon } from "lucide-react";
import {
  Activity,
  Banknote,
  CircleAlert,
  CircleCheck,
  CircleX,
  Clock3,
  History,
  MonitorPlay,
  Server,
  ServerOff,
  Settings2,
  ShieldAlert,
  ShieldCheck,
  UserPlus,
  Users,
  Wallet,
} from "lucide-react";
import { AdminCard, AdminStatCard, AdminStatGrid } from "@/components/admin/admin-page";
import { MachineAvailabilityChart } from "@/components/admin/machine-availability-chart";
import { MembersChart } from "@/components/admin/members-chart";
import { RentalCharts } from "@/components/admin/rental-charts";
import { TopupCharts } from "@/components/admin/topups/topup-charts";
import {
  ComparisonHint,
  LatestMembersCard,
  LatestRentalsCard,
  LatestTopupsCard,
  rentalsHref,
  topupsHref,
} from "@/components/admin/overview/overview-lists";
import { Alert, AlertDescription, AlertTitle } from "@/components/ui/alert";
import {
  CardContent,
  CardDescription,
  CardHeader,
  CardTitle,
} from "@/components/ui/card";
import {
  getAdminOverviewMachines,
  getAdminOverviewMembers,
  getAdminOverviewRentals,
  getAdminOverviewStatus,
  getAdminOverviewTopups,
  getAdminOverviewTotals,
} from "@/lib/admin-overview";
import { formatBangkokDateTime } from "@/lib/date-range";
import { formatBalance } from "@/lib/money";

/**
 * Async server sections for `/admin/overview`. Each section owns one loader and
 * is rendered inside its own `<Suspense>` boundary by the page, so the
 * Controller-backed machine section streams independently from the
 * database-backed money sections.
 */

export type OverviewSectionSearchParams = Promise<
  Record<string, string | string[] | undefined>
>;

type StatTone = "primary" | "available" | "busy" | "maintenance" | "muted";

type OverviewStatCard = {
  key: string;
  label: string;
  value: React.ReactNode;
  hint: React.ReactNode;
  icon: LucideIcon;
  tone: StatTone;
  href?: string;
};

function StatCards({
  cards,
  className,
}: {
  cards: OverviewStatCard[];
  className?: string;
}) {
  return (
    <AdminStatGrid className={className}>
      {cards.map((card) => (
        <AdminStatCard
          key={card.key}
          label={card.label}
          value={card.value}
          hint={card.hint}
          icon={card.icon}
          tone={card.tone}
          href={card.href}
        />
      ))}
    </AdminStatGrid>
  );
}

export async function OverviewStatusSection({
  searchParams,
}: {
  searchParams: OverviewSectionSearchParams;
}) {
  const data = await getAdminOverviewStatus(await searchParams);
  return (
    <div className="flex flex-col gap-2">
      {data.rangeError ? (
        <Alert variant="destructive">
          <CircleAlert aria-hidden />
          <AlertTitle>ช่วงเวลาที่เลือกไม่ถูกต้อง</AlertTitle>
          <AlertDescription>{data.rangeError}</AlertDescription>
        </Alert>
      ) : null}
      <p className="text-xs text-muted-foreground" role="status">
        ข้อมูล ณ เวลาที่โหลด: {formatBangkokDateTime(data.loadedAt)} · ช่วงข้อมูล: {data.range.label}
      </p>
    </div>
  );
}

export async function OverviewTotalsSection({
  searchParams,
}: {
  searchParams: OverviewSectionSearchParams;
}) {
  const data = await getAdminOverviewTotals(await searchParams);
  const { memberTotals, signups, topupTotals } = data;
  const cards: OverviewStatCard[] = [
    {
      key: "members",
      label: "สมาชิกทั้งหมด",
      value: memberTotals.members.toLocaleString("th-TH"),
      hint: "บัญชีในระบบ",
      icon: Users,
      tone: "primary",
      href: "/admin/users",
    },
    {
      key: "admins",
      label: "ผู้ดูแลระบบ",
      value: memberTotals.admins.toLocaleString("th-TH"),
      hint: "บัญชีที่มีสิทธิ์ admin",
      icon: ShieldCheck,
      tone: "available",
      href: "/admin/users?role=admin",
    },
    {
      key: "balance",
      label: "ยอดเงินสมาชิกทั้งหมด",
      value: formatBalance(memberTotals.totalBalanceSatang),
      hint: "ยอดคงเหลือรวมทุกบัญชี",
      icon: Wallet,
      tone: "available",
      href: "/admin/users",
    },
    {
      key: "signups",
      label: "สมาชิกใหม่ในช่วงที่เลือก",
      value: signups.newMembers.toLocaleString("th-TH"),
      hint: (
        <ComparisonHint current={signups.newMembers} previous={signups.previousMembers} />
      ),
      icon: UserPlus,
      tone: "primary",
      href: "/admin/users?sort=newest",
    },
  ];
  return (
    <div className="flex flex-col gap-2">
      <StatCards cards={cards} />
      <p className="text-xs text-muted-foreground">
        เติมเงินสะสมทั้งหมด: {formatBalance(topupTotals.creditedSatang)} จาก{" "}
        {topupTotals.completed.toLocaleString("th-TH")} รายการที่สำเร็จ (ทุกช่วงเวลา)
      </p>
    </div>
  );
}

export async function OverviewMembersSection({
  searchParams,
}: {
  searchParams: OverviewSectionSearchParams;
}) {
  const data = await getAdminOverviewMembers(await searchParams);
  return (
    <div className="flex min-w-0 flex-col gap-3">
      <MembersChart data={data.chart} from={data.range.from} to={data.range.to} />
      <LatestMembersCard members={data.latest} href="/admin/users" />
    </div>
  );
}

export async function OverviewTopupsSection({
  searchParams,
}: {
  searchParams: OverviewSectionSearchParams;
}) {
  const data = await getAdminOverviewTopups(await searchParams);
  const cards: OverviewStatCard[] = [
    {
      key: "credited",
      label: "เงินที่เติมเข้าระบบจริง",
      value: formatBalance(data.stats.creditedSatang),
      hint: (
        <ComparisonHint
          current={data.stats.creditedSatang}
          previous={data.previous.creditedSatang}
        />
      ),
      icon: Banknote,
      tone: "available",
      href: topupsHref(data.range, { status: "completed" }),
    },
    {
      key: "completed",
      label: "รายการที่สำเร็จในช่วง",
      value: data.stats.completed.toLocaleString("th-TH"),
      hint: (
        <ComparisonHint
          current={data.stats.completed}
          previous={data.previous.completed}
        />
      ),
      icon: CircleCheck,
      tone: "available",
      href: topupsHref(data.range, { status: "completed" }),
    },
    {
      key: "pending",
      label: "รอดำเนินการ",
      value: data.stats.pending.toLocaleString("th-TH"),
      hint: (
        <ComparisonHint current={data.stats.pending} previous={data.previous.pending} />
      ),
      icon: Clock3,
      tone: "busy",
      href: topupsHref(data.range, { status: "pending" }),
    },
    {
      key: "failed",
      label: "ไม่สำเร็จ/หมดอายุ",
      value: data.stats.failed.toLocaleString("th-TH"),
      hint: (
        <ComparisonHint
          current={data.stats.failed}
          previous={data.previous.failed}
          invert
        />
      ),
      icon: CircleX,
      tone: "maintenance",
      href: topupsHref(data.range, { status: "failed" }),
    },
    {
      key: "slip",
      label: "สลิปรอตรวจสอบ",
      value: data.pendingSlipReviews.toLocaleString("th-TH"),
      hint: `รายการ PromptPay/โอนธนาคารที่รอผู้ดูแลตรวจ · สะสมทั้งหมด ${data.totals.total.toLocaleString("th-TH")} รายการ`,
      icon: ShieldAlert,
      tone: "busy",
      href: topupsHref(data.range, { status: "pending" }),
    },
  ];
  return (
    <div className="flex min-w-0 flex-col gap-3">
      <StatCards cards={cards} className="sm:grid-cols-2 lg:grid-cols-3 xl:grid-cols-5" />
      <TopupCharts chart={data.chart} previous={data.chartPrevious} />
      <LatestTopupsCard entries={data.latest} href={topupsHref(data.range)} />
    </div>
  );
}

export async function OverviewRentalsSection({
  searchParams,
}: {
  searchParams: OverviewSectionSearchParams;
}) {
  const data = await getAdminOverviewRentals(await searchParams);
  const { current, previous, summary } = data;
  const cards: OverviewStatCard[] = [
    {
      key: "net",
      label: "รายได้สุทธิจากการเช่า",
      value: formatBalance(current.revenueSatang),
      hint: (
        <span className="flex flex-col gap-1">
          <ComparisonHint
            current={current.revenueSatang}
            previous={previous.revenueSatang}
          />
          <span>
            สะสมทั้งหมด {formatBalance(summary.revenueSatang)} ·{" "}
            {summary.totalRentals.toLocaleString("th-TH")} รายการ
          </span>
        </span>
      ),
      icon: Wallet,
      tone: "primary",
      href: rentalsHref(data.range),
    },
    {
      key: "paid",
      label: "ยอดชำระรวมในช่วง",
      value: formatBalance(current.paidSatang),
      hint: <ComparisonHint current={current.paidSatang} previous={previous.paidSatang} />,
      icon: Banknote,
      tone: "available",
      href: rentalsHref(data.range),
    },
    {
      key: "refunded",
      label: "คืนเงินรวมในช่วง",
      value: formatBalance(current.refundedSatang),
      hint: (
        <ComparisonHint
          current={current.refundedSatang}
          previous={previous.refundedSatang}
          invert
        />
      ),
      icon: History,
      tone: "maintenance",
      href: rentalsHref(data.range),
    },
  ];
  return (
    <div className="flex min-w-0 flex-col gap-3">
      <StatCards cards={cards} className="sm:grid-cols-2 xl:grid-cols-3" />
      <RentalCharts
        chart={data.chart}
        chartPrevious={{ revenueSatang: previous.revenueSatang, count: previous.rentalCount }}
        current={{
          revenueSatang: current.revenueSatang,
          rentalCount: current.rentalCount,
          paidSatang: current.paidSatang,
          refundedSatang: current.refundedSatang,
        }}
        previous={{
          revenueSatang: previous.revenueSatang,
          rentalCount: previous.rentalCount,
          paidSatang: previous.paidSatang,
          refundedSatang: previous.refundedSatang,
        }}
      />
      <LatestRentalsCard entries={data.latest} href={rentalsHref(data.range)} />
    </div>
  );
}

export async function OverviewMachinesSection() {
  const data = await getAdminOverviewMachines();
  const { summary, available } = data;
  const show = (value: number) =>
    available ? value.toLocaleString("th-TH") : "—";
  const statusHint = available ? undefined : "ติดต่อ Controller ไม่ได้";
  const cards: OverviewStatCard[] = [
    {
      key: "rentable",
      label: "พร้อมให้เช่า",
      value: show(summary.rentable),
      hint: statusHint ?? `ออนไลน์ ${summary.online.toLocaleString("th-TH")} · ออฟไลน์ ${summary.offline.toLocaleString("th-TH")}`,
      icon: CircleCheck,
      tone: "available",
      href: "/admin/machines",
    },
    {
      key: "total",
      label: "เครื่องทั้งหมด",
      value: show(summary.total),
      hint: statusHint ?? `อนุมัติแล้ว ${summary.approved.toLocaleString("th-TH")} · รออนุมัติ ${summary.pendingApproval.toLocaleString("th-TH")}`,
      icon: Server,
      tone: "muted",
      href: "/admin/machines",
    },
    {
      key: "online",
      label: "ออนไลน์ตอนนี้",
      value: show(summary.online),
      hint: statusHint ?? `ออฟไลน์ ${summary.offline.toLocaleString("th-TH")} · ปลุกได้ ${summary.wakeReadyOffline.toLocaleString("th-TH")}`,
      icon: Activity,
      tone: "primary",
      href: "/admin/machines",
    },
    {
      key: "busy",
      label: "กำลังใช้งาน",
      value: show(summary.busy),
      hint: statusHint ?? `กำลังเตรียม/เคลียร์ ${summary.preparing.toLocaleString("th-TH")} · ไม่ว่าง ${summary.idle.toLocaleString("th-TH")}`,
      icon: MonitorPlay,
      tone: "busy",
      href: "/admin/machines",
    },
  ];
  const detailRows = [
    { key: "pending", label: "รออนุมัติ", value: summary.pendingApproval, icon: ShieldAlert },
    { key: "preparing", label: "กำลังเตรียม/เคลียร์", value: summary.preparing, icon: Clock3 },
    { key: "idle", label: "ออนไลน์แต่ไม่ว่าง", value: summary.idle, icon: MonitorPlay },
    { key: "maintenance", label: "ซ่อมบำรุง", value: summary.maintenance, icon: Settings2 },
    { key: "disabled", label: "ปิดให้เช่า", value: summary.disabled, icon: ServerOff },
    { key: "unknown", label: "ไม่ทราบสถานะ", value: summary.unknownState, icon: CircleAlert },
  ];
  return (
    <div className="flex min-w-0 flex-col gap-3" aria-busy={false}>
      {!available ? (
        <Alert>
          <CircleAlert aria-hidden />
          <AlertTitle>ติดต่อระบบเครื่องไม่ได้</AlertTitle>
          <AlertDescription>
            กำลังแสดงเฉพาะข้อมูลการเงินของระบบ สถานะเครื่องจาก Controller จะกลับมาเมื่อเชื่อมต่อได้
          </AlertDescription>
        </Alert>
      ) : null}
      <StatCards cards={cards} />
      <div className="grid min-w-0 items-start gap-3 xl:grid-cols-2">
        <MachineAvailabilityChart summary={summary} available={available} />
        <AdminCard className="h-full gap-3">
          <CardHeader className="gap-1">
            <CardTitle>รายละเอียดสถานะเครื่อง</CardTitle>
            <CardDescription>
              ข้อมูลจาก Controller ณ {formatBangkokDateTime(data.now)} · ไม่นับเครื่องรออนุมัติในสัดส่วน
            </CardDescription>
          </CardHeader>
          <CardContent>
            <dl className="grid gap-2 sm:grid-cols-2">
              {detailRows.map(({ key, label, value, icon: Icon }) => (
                <div
                  key={key}
                  className="flex items-center justify-between gap-2 rounded-xl border px-3 py-2 text-sm"
                >
                  <dt className="flex items-center gap-2 text-muted-foreground">
                    <Icon aria-hidden className="size-4" />
                    {label}
                  </dt>
                  <dd className="font-medium tabular-nums">
                    {available ? value.toLocaleString("th-TH") : "—"}
                  </dd>
                </div>
              ))}
            </dl>
            <div className="mt-3 rounded-xl border px-3 py-2 text-sm">
              <p className="font-medium">ความพร้อมของ Agent</p>
              <p className="mt-1 text-muted-foreground">
                พร้อม {show(summary.readiness.ready)} · ยังไม่พร้อม{" "}
                {show(summary.readiness.notReady)} · ติดต่อไม่ได้{" "}
                {show(summary.readiness.unreachable)} · ยังไม่ยืนยัน{" "}
                {show(summary.readiness.unknown)}
              </p>
            </div>
          </CardContent>
        </AdminCard>
      </div>
    </div>
  );
}
