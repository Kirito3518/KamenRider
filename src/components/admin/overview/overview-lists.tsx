import Link from "next/link";
import { ArrowRight, Minus, TrendingDown, TrendingUp } from "lucide-react";
import { AdminCard } from "@/components/admin/admin-page";
import { MemberTable } from "@/components/admin/member-table";
import {
  CHANNEL_META,
  entryAmount,
  percentChange,
} from "@/components/admin/topups/topup-labels";
import { topupStatusLabel, topupStatusTone } from "@/components/topup/topup-display";
import { Badge } from "@/components/ui/badge";
import { Button } from "@/components/ui/button";
import {
  CardAction,
  CardContent,
  CardDescription,
  CardHeader,
  CardTitle,
} from "@/components/ui/card";
import {
  Empty,
  EmptyDescription,
  EmptyHeader,
  EmptyTitle,
} from "@/components/ui/empty";
import {
  Table,
  TableBody,
  TableCell,
  TableHead,
  TableHeader,
  TableRow,
} from "@/components/ui/table";
import type { AdminMember } from "@/lib/admin";
import type {
  OverviewRentalEntry,
} from "@/lib/admin-overview";
import { formatBangkokDateTime, type ResolvedDateRange } from "@/lib/date-range";
import { formatBalance } from "@/lib/money";
import type { AdminTopupEntry } from "@/lib/topup/types";

/**
 * Server-rendered overview list cards and small shared hints. These components
 * are intentionally not client components: the dashboard lists are links to the
 * existing detail/history screens and need no interactivity.
 */

export function overviewPeriodQuery(range: ResolvedDateRange) {
  const params = new URLSearchParams();
  params.set("period", range.period);
  if (range.period === "custom") {
    params.set("from", range.from);
    params.set("to", range.to);
  }
  return params;
}

export function topupsHref(range: ResolvedDateRange, extra?: Record<string, string>) {
  const params = overviewPeriodQuery(range);
  for (const [key, value] of Object.entries(extra ?? {})) {
    if (value && value !== "all") params.set(key, value);
  }
  return `/admin/topups?${params.toString()}`;
}

export function rentalsHref(range: ResolvedDateRange, extra?: Record<string, string>) {
  const params = overviewPeriodQuery(range);
  for (const [key, value] of Object.entries(extra ?? {})) {
    if (value && value !== "all") params.set(key, value);
  }
  return `/admin/rentals?${params.toString()}`;
}

/** Percent change badge with the same wording as the top-up/member screens. */
export function ComparisonHint({
  current,
  previous,
  invert = false,
}: {
  current: number;
  previous: number;
  invert?: boolean;
}) {
  const percent = percentChange(current, previous);
  if (percent === null) {
    return <span>ยังไม่มีข้อมูลเปรียบเทียบ</span>;
  }
  const rounded = Math.abs(percent) < 0.05 ? 0 : percent;
  const direction = rounded > 0 ? "up" : rounded < 0 ? "down" : "flat";
  const good =
    direction === "flat" ? null : invert ? direction === "down" : direction === "up";
  const Icon = direction === "up" ? TrendingUp : direction === "down" ? TrendingDown : Minus;
  return (
    <span className="inline-flex flex-wrap items-center gap-1.5">
      <Badge variant={good === null ? "secondary" : good ? "available" : "destructive"}>
        <Icon aria-hidden />
        {`${rounded > 0 ? "+" : ""}${rounded.toFixed(1)}%`}
      </Badge>
      <span>เทียบช่วงก่อนหน้า</span>
    </span>
  );
}

function SectionAction({ href, label }: { href: string; label: string }) {
  return (
    <CardAction>
      <Button variant="outline" size="sm" nativeButton={false} render={<Link href={href} />}>
        {label}
        <ArrowRight data-icon="inline-end" aria-hidden />
      </Button>
    </CardAction>
  );
}

export function LatestMembersCard({
  members,
  href,
}: {
  members: AdminMember[];
  href: string;
}) {
  return (
    <AdminCard className="h-full">
      <CardHeader>
        <CardTitle>สมาชิกที่สมัครล่าสุด</CardTitle>
        <CardDescription>แสดง 5 บัญชีล่าสุดในระบบ</CardDescription>
        <SectionAction href={href} label="ดูสมาชิก" />
      </CardHeader>
      <CardContent>
        <MemberTable members={members} />
      </CardContent>
    </AdminCard>
  );
}

export function LatestTopupsCard({
  entries,
  href,
}: {
  entries: AdminTopupEntry[];
  href: string;
}) {
  return (
    <AdminCard className="h-full">
      <CardHeader>
        <CardTitle>รายการเติมเงินล่าสุด</CardTitle>
        <CardDescription>5 รายการล่าสุดตามเวลาที่สร้าง · เวลาไทย</CardDescription>
        <SectionAction href={href} label="ดูประวัติเติมเงิน" />
      </CardHeader>
      <CardContent>
        {entries.length === 0 ? (
          <Empty className="border-0 py-8">
            <EmptyHeader>
              <EmptyTitle>ยังไม่มีรายการเติมเงิน</EmptyTitle>
              <EmptyDescription>รายการจะแสดงเมื่อมีผู้ใช้สร้างรายการเติมเงิน</EmptyDescription>
            </EmptyHeader>
          </Empty>
        ) : (
          <div className="overflow-x-auto">
            <Table>
              <caption className="sr-only">รายการเติมเงินล่าสุด 5 รายการ</caption>
              <TableHeader>
                <TableRow>
                  <TableHead>ผู้ใช้</TableHead>
                  <TableHead>ช่องทาง</TableHead>
                  <TableHead className="text-right">ยอดเงิน</TableHead>
                  <TableHead>สถานะ</TableHead>
                  <TableHead>เวลา</TableHead>
                  <TableHead className="text-right">รายละเอียด</TableHead>
                </TableRow>
              </TableHeader>
              <TableBody>
                {entries.map((entry) => {
                  const meta = CHANNEL_META[entry.channel];
                  const Icon = meta.icon;
                  const amount = entryAmount(entry);
                  return (
                    <TableRow key={entry.id}>
                      <TableCell className="min-w-36">
                        <span className="block max-w-48 truncate font-medium">{entry.userName}</span>
                        <span className="block max-w-48 truncate text-xs text-muted-foreground">
                          {entry.userEmail}
                        </span>
                      </TableCell>
                      <TableCell>
                        <span className="inline-flex items-center gap-1.5 whitespace-nowrap text-sm">
                          <Icon aria-hidden className="size-3.5 text-muted-foreground" />
                          {meta.label}
                        </span>
                      </TableCell>
                      <TableCell className="text-right font-medium tabular-nums">
                        {amount === null ? "—" : formatBalance(amount)}
                      </TableCell>
                      <TableCell>
                        <Badge variant={topupStatusTone(entry.status)}>
                          {topupStatusLabel(entry.status)}
                        </Badge>
                      </TableCell>
                      <TableCell className="whitespace-nowrap text-xs text-muted-foreground">
                        {formatBangkokDateTime(entry.createdAt)}
                      </TableCell>
                      <TableCell className="text-right">
                        <Button
                          variant="outline"
                          size="icon-sm"
                          nativeButton={false}
                          aria-label={`ดูรายการเติมเงิน ${entry.reference}`}
                          title="ดูรายละเอียด"
                          render={
                            <Link href={`/admin/topups?q=${encodeURIComponent(entry.reference)}`} />
                          }
                        >
                          <ArrowRight aria-hidden />
                        </Button>
                      </TableCell>
                    </TableRow>
                  );
                })}
              </TableBody>
            </Table>
          </div>
        )}
      </CardContent>
    </AdminCard>
  );
}

const rentalStatusLabels: Record<string, string> = {
  pending: "รอยืนยัน",
  confirmed: "ยืนยันแล้ว",
  rejected: "คืนเงิน",
};

function rentalStatusTone(status: string): "available" | "busy" | "destructive" | "secondary" {
  switch (status) {
    case "confirmed":
      return "available";
    case "pending":
      return "busy";
    case "rejected":
      return "destructive";
    default:
      return "secondary";
  }
}

export function LatestRentalsCard({
  entries,
  href,
}: {
  entries: OverviewRentalEntry[];
  href: string;
}) {
  return (
    <AdminCard className="h-full">
      <CardHeader>
        <CardTitle>รายการเช่าล่าสุด</CardTitle>
        <CardDescription>5 รายการชำระเงินล่าสุดจากฐานข้อมูล · เวลาไทย</CardDescription>
        <SectionAction href={href} label="ดูประวัติการเช่า" />
      </CardHeader>
      <CardContent>
        {entries.length === 0 ? (
          <Empty className="border-0 py-8">
            <EmptyHeader>
              <EmptyTitle>ยังไม่มีรายการเช่า</EmptyTitle>
              <EmptyDescription>เมื่อมีผู้เช่าเครื่อง รายการและยอดเงินจะแสดงที่นี่</EmptyDescription>
            </EmptyHeader>
          </Empty>
        ) : (
          <div className="overflow-x-auto">
            <Table>
              <caption className="sr-only">รายการเช่าล่าสุด 5 รายการ</caption>
              <TableHeader>
                <TableRow>
                  <TableHead>ผู้เช่า</TableHead>
                  <TableHead>เครื่อง</TableHead>
                  <TableHead className="text-right">ยอดสุทธิ</TableHead>
                  <TableHead>สถานะ</TableHead>
                  <TableHead>เวลา</TableHead>
                  <TableHead className="text-right">รายละเอียด</TableHead>
                </TableRow>
              </TableHeader>
              <TableBody>
                {entries.map((entry) => (
                  <TableRow key={entry.id}>
                    <TableCell className="min-w-36">
                      <span className="block max-w-48 truncate font-medium">{entry.userName}</span>
                      <span className="block max-w-48 truncate text-xs text-muted-foreground">
                        {entry.userEmail || entry.userId}
                      </span>
                    </TableCell>
                    <TableCell className="min-w-32">
                      <span className="block max-w-40 truncate">{entry.machineName}</span>
                      <span className="block max-w-40 truncate font-mono text-xs text-muted-foreground">
                        {entry.id}
                      </span>
                    </TableCell>
                    <TableCell className="text-right font-medium tabular-nums">
                      {formatBalance(entry.netSatang)}
                    </TableCell>
                    <TableCell>
                      <Badge variant={rentalStatusTone(entry.status)}>
                        {rentalStatusLabels[entry.status] ?? entry.status}
                      </Badge>
                    </TableCell>
                    <TableCell className="whitespace-nowrap text-xs text-muted-foreground">
                      {formatBangkokDateTime(entry.createdAt)}
                    </TableCell>
                    <TableCell className="text-right">
                      <Button
                        variant="outline"
                        size="icon-sm"
                        nativeButton={false}
                        aria-label={`ดูรายการเช่า ${entry.id}`}
                        title="ดูรายละเอียด"
                        render={<Link href={`/admin/rentals?q=${encodeURIComponent(entry.id)}`} />}
                      >
                        <ArrowRight aria-hidden />
                      </Button>
                    </TableCell>
                  </TableRow>
                ))}
              </TableBody>
            </Table>
          </div>
        )}
      </CardContent>
    </AdminCard>
  );
}
