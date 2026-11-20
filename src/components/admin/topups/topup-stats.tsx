"use client";

import {
  Banknote,
  CircleCheck,
  CircleX,
  Clock3,
  ListOrdered,
  Minus,
  TrendingDown,
  TrendingUp,
  Wallet,
  type LucideIcon,
} from "lucide-react";
import { motion, useReducedMotion } from "motion/react";
import { AdminStatCard } from "@/components/admin/admin-page";
import { Badge } from "@/components/ui/badge";
import { formatBalance } from "@/lib/money";
import type { AdminTopupStats } from "@/lib/topup/types";
import { percentChange } from "@/components/admin/topups/topup-labels";

type StatTone = "primary" | "available" | "busy" | "maintenance" | "muted";

type StatCard = {
  key: string;
  label: string;
  value: string;
  current: number;
  previous: number;
  icon: LucideIcon;
  tone: StatTone;
  /** When true a rise is bad (e.g. failed transactions). */
  invert?: boolean;
};

/**
 * Six KPI cards. Comparisons only render when the previous period actually has
 * data; otherwise the card says there is nothing to compare yet.
 */
export function TopupStats({
  stats,
  previous,
}: {
  stats: AdminTopupStats;
  previous: AdminTopupStats;
}) {
  const reduceMotion = useReducedMotion();
  const cards: StatCard[] = [
    {
      key: "requested",
      label: "ยอดที่ขอเติมรวม",
      value: formatBalance(stats.requestedSatang),
      current: stats.requestedSatang,
      previous: previous.requestedSatang,
      icon: Wallet,
      tone: "primary",
    },
    {
      key: "total",
      label: "รายการทั้งหมด",
      value: stats.total.toLocaleString("th-TH"),
      current: stats.total,
      previous: previous.total,
      icon: ListOrdered,
      tone: "muted",
    },
    {
      key: "completed",
      label: "สำเร็จ",
      value: stats.completed.toLocaleString("th-TH"),
      current: stats.completed,
      previous: previous.completed,
      icon: CircleCheck,
      tone: "available",
    },
    {
      key: "pending",
      label: "รอดำเนินการ",
      value: stats.pending.toLocaleString("th-TH"),
      current: stats.pending,
      previous: previous.pending,
      icon: Clock3,
      tone: "busy",
    },
    {
      key: "failed",
      label: "ไม่สำเร็จ",
      value: stats.failed.toLocaleString("th-TH"),
      current: stats.failed,
      previous: previous.failed,
      icon: CircleX,
      tone: "maintenance",
      invert: true,
    },
    {
      key: "credited",
      label: "เงินที่เติมเข้าระบบจริง",
      value: formatBalance(stats.creditedSatang),
      current: stats.creditedSatang,
      previous: previous.creditedSatang,
      icon: Banknote,
      tone: "available",
    },
  ];

  return (
    <div className="grid gap-3 sm:grid-cols-2 lg:grid-cols-3 2xl:grid-cols-6">
      {cards.map((card, index) => (
        <motion.div
          key={card.key}
          className="min-w-0"
          initial={reduceMotion ? false : { opacity: 0, y: 8 }}
          animate={{ opacity: 1, y: 0 }}
          transition={reduceMotion ? { duration: 0 } : { duration: 0.25, delay: index * 0.04 }}
        >
          <AdminStatCard
            label={card.label}
            value={card.value}
            hint={
              <ComparisonHint
                current={card.current}
                previous={card.previous}
                invert={card.invert}
              />
            }
            icon={card.icon}
            tone={card.tone}
          />
        </motion.div>
      ))}
    </div>
  );
}

function ComparisonHint({
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
  const text = `${rounded > 0 ? "+" : ""}${rounded.toFixed(1)}%`;
  return (
    <span className="inline-flex flex-wrap items-center gap-1.5">
      <Badge variant={good === null ? "secondary" : good ? "available" : "destructive"}>
        <Icon aria-hidden />
        {text}
      </Badge>
      <span>เทียบช่วงก่อนหน้า</span>
    </span>
  );
}
