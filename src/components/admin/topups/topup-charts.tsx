"use client";

import { useReducedMotion } from "motion/react";
import { motion } from "motion/react";
import { Bar, BarChart, CartesianGrid, XAxis, YAxis } from "recharts";
import { AdminCard } from "@/components/admin/admin-page";
import { CardContent, CardDescription, CardHeader, CardTitle } from "@/components/ui/card";
import {
  ChartContainer,
  ChartLegend,
  ChartLegendContent,
  ChartTooltip,
  ChartTooltipContent,
  type ChartConfig,
} from "@/components/ui/chart";
import { formatBalance } from "@/lib/money";
import type { TopupChannel } from "@/lib/topup/constants";
import type { TopupChartPoint } from "@/lib/topup/types";
import { CHANNEL_META, channelLabel } from "@/components/admin/topups/topup-labels";

type SeriesKey = "truemoney" | "promptpay" | "bankTransfer" | "coupon";

const SERIES: { dataKey: SeriesKey; channel: TopupChannel }[] = [
  { dataKey: "truemoney", channel: "truemoney" },
  { dataKey: "promptpay", channel: "promptpay" },
  { dataKey: "bankTransfer", channel: "bank_transfer" },
  { dataKey: "coupon", channel: "coupon" },
];

/** Chart colours are semantic tokens exposed through the shadcn chart config. */
const chartConfig = {
  truemoney: {
    label: CHANNEL_META.truemoney.label,
    color: CHANNEL_META.truemoney.color,
  },
  promptpay: {
    label: CHANNEL_META.promptpay.label,
    color: CHANNEL_META.promptpay.color,
  },
  bankTransfer: {
    label: CHANNEL_META.bank_transfer.label,
    color: CHANNEL_META.bank_transfer.color,
  },
  coupon: {
    label: CHANNEL_META.coupon.label,
    color: CHANNEL_META.coupon.color,
  },
} satisfies ChartConfig;

type ChartRow = {
  key: string;
  label: string;
} & Record<SeriesKey, number>;

function amountRows(chart: TopupChartPoint[]): ChartRow[] {
  return chart.map((point) => ({
    key: point.key,
    label: point.label,
    truemoney: point.truemoneySatang,
    promptpay: point.promptpaySatang,
    bankTransfer: point.bankTransferSatang,
    coupon: point.couponSatang,
  }));
}

function countRows(chart: TopupChartPoint[]): ChartRow[] {
  return chart.map((point) => ({
    key: point.key,
    label: point.label,
    truemoney: point.truemoneyCount,
    promptpay: point.promptpayCount,
    bankTransfer: point.bankTransferCount,
    coupon: point.couponCount,
  }));
}

function rowTotal(row: ChartRow): number {
  return row.truemoney + row.promptpay + row.bankTransfer + row.coupon;
}

function hasValues(rows: ChartRow[]): boolean {
  return rows.some((row) => SERIES.some((series) => row[series.dataKey] > 0));
}

function rowsSummary(rows: ChartRow[], kind: "amount" | "count"): string {
  return rows
    .map((row) => {
      const parts = SERIES.filter((series) => row[series.dataKey] > 0).map(
        (series) =>
          `${channelLabel(series.channel)} ${
            kind === "amount"
              ? formatBalance(row[series.dataKey])
              : `${row[series.dataKey].toLocaleString("th-TH")} รายการ`
          }`,
      );
      return `${row.label}: ${parts.length ? parts.join(", ") : "ไม่มีข้อมูล"}`;
    })
    .join("; ");
}

function amountTooltipFormatter(value: unknown, name: unknown) {
  const key = String(name ?? "");
  const series = SERIES.find((item) => item.dataKey === key);
  const numeric = Number(value);
  return (
    <div className="flex w-full items-center justify-between gap-3">
      <span className="text-muted-foreground">
        {series ? channelLabel(series.channel) : key}
      </span>
      <span className="font-mono font-medium text-foreground tabular-nums">
        {Number.isFinite(numeric) ? formatBalance(numeric) : "—"}
      </span>
    </div>
  );
}

export function TopupCharts({
  chart,
  previous,
}: {
  chart: TopupChartPoint[];
  previous: { requestedSatang: number; creditedSatang: number; total: number };
}) {
  const amounts = amountRows(chart);
  const counts = countRows(chart);
  const currentAmount = amounts.reduce((total, row) => total + rowTotal(row), 0);
  const currentCount = counts.reduce((total, row) => total + rowTotal(row), 0);
  return (
    <div className="grid min-w-0 gap-3 xl:grid-cols-2">
      <ChannelStackedChart
        index={0}
        kind="amount"
        title="ยอดเงินที่เติมสำเร็จตามช่องทาง"
        description="ยอดเงินที่เติมเข้าระบบจริงในช่วงที่เลือก · แยกตามช่องทาง · เวลาไทย"
        comparison={comparisonLine(currentAmount, previous.creditedSatang, "amount")}
        rows={amounts}
        emptyLabel="ยังไม่มียอดเงินที่เติมสำเร็จในช่วงที่เลือก"
      />
      <ChannelStackedChart
        index={1}
        kind="count"
        title="จำนวนรายการตามช่องทาง"
        description="จำนวนรายการที่สร้างในช่วงที่เลือก · แยกตามช่องทาง · เวลาไทย"
        comparison={comparisonLine(currentCount, previous.total, "count")}
        rows={counts}
        emptyLabel="ยังไม่มีรายการเติมเงินในช่วงที่เลือก"
      />
    </div>
  );
}

function comparisonLine(
  current: number,
  previous: number,
  kind: "amount" | "count",
): string {
  if (previous <= 0) return "ยังไม่มีข้อมูลช่วงก่อนหน้าให้เปรียบเทียบ";
  const change = ((current - previous) / previous) * 100;
  const previousLabel =
    kind === "amount" ? formatBalance(previous) : `${previous.toLocaleString("th-TH")} รายการ`;
  return `ช่วงก่อนหน้า: ${previousLabel} (${change >= 0 ? "+" : ""}${change.toFixed(1)}%)`;
}

function ChannelStackedChart({
  index,
  kind,
  title,
  description,
  comparison,
  rows,
  emptyLabel,
}: {
  index: number;
  kind: "amount" | "count";
  title: string;
  description: string;
  comparison: string;
  rows: ChartRow[];
  emptyLabel: string;
}) {
  const reduceMotion = useReducedMotion();
  const populated = hasValues(rows);
  return (
    <motion.section
      className="min-w-0"
      initial={reduceMotion ? false : { opacity: 0, y: 8 }}
      animate={{ opacity: 1, y: 0 }}
      transition={
        reduceMotion ? { duration: 0 } : { duration: 0.25, delay: 0.08 + index * 0.05 }
      }
    >
      <AdminCard className="h-full gap-3">
        <CardHeader className="gap-1">
          <CardTitle>{title}</CardTitle>
          <CardDescription>{description}</CardDescription>
          <p className="text-xs text-muted-foreground">{comparison}</p>
        </CardHeader>
        <CardContent>
          {populated ? (
            <ChartContainer
              config={chartConfig}
              className="h-64 w-full aspect-auto"
              role="region"
              aria-label={title}
            >
              <BarChart
                accessibilityLayer
                data={rows}
                margin={{ top: 8, right: 8, bottom: 0, left: 0 }}
              >
                <CartesianGrid vertical={false} strokeDasharray="3 4" />
                <XAxis
                  dataKey="label"
                  tickLine={false}
                  axisLine={false}
                  tickMargin={10}
                  minTickGap={16}
                  tick={{ fontSize: 11, fill: "var(--muted-foreground)" }}
                />
                <YAxis
                  allowDecimals={kind === "count" ? false : undefined}
                  tickLine={false}
                  axisLine={false}
                  tickMargin={8}
                  width={kind === "amount" ? 84 : 36}
                  tick={{ fontSize: 10, fill: "var(--muted-foreground)" }}
                  tickFormatter={(value: number) =>
                    kind === "amount"
                      ? formatBalance(Number(value))
                      : Number(value).toLocaleString("th-TH")
                  }
                />
                <ChartTooltip
                  cursor={false}
                  content={
                    kind === "amount" ? (
                      <ChartTooltipContent formatter={amountTooltipFormatter} />
                    ) : (
                      <ChartTooltipContent />
                    )
                  }
                />
                <ChartLegend content={<ChartLegendContent />} />
                {SERIES.map((series) => (
                  <Bar
                    key={series.dataKey}
                    dataKey={series.dataKey}
                    stackId="channel"
                    fill={`var(--color-${series.dataKey})`}
                    isAnimationActive={false}
                  />
                ))}
              </BarChart>
            </ChartContainer>
          ) : (
            <p className="flex h-64 items-center justify-center rounded-2xl border border-dashed px-6 text-center text-sm text-muted-foreground">
              {emptyLabel}
            </p>
          )}
          <p className="sr-only">{rowsSummary(rows, kind)}</p>
        </CardContent>
      </AdminCard>
    </motion.section>
  );
}
