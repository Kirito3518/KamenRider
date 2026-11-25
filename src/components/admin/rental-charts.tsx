"use client";

import { Bar, BarChart, CartesianGrid, XAxis, YAxis } from "recharts";
import {
  Card,
  CardContent,
  CardDescription,
  CardHeader,
  CardTitle,
} from "@/components/ui/card";
import {
  ChartContainer,
  ChartTooltip,
  ChartTooltipContent,
  type ChartConfig,
} from "@/components/ui/chart";
import { formatBalance } from "@/lib/money";
import type { RentalChartPoint } from "@/lib/rental-history";

/**
 * Rental revenue/count bar charts shared by `/admin/rentals` and the admin
 * overview. They render from the payment-table aggregates only; Controller
 * lease data never reaches this component.
 */

const revenueConfig = {
  revenueBaht: { label: "รายได้สุทธิ (บาท)", color: "var(--primary)" },
} satisfies ChartConfig;

const countConfig = {
  count: { label: "จำนวนรายการ", color: "var(--status-busy)" },
} satisfies ChartConfig;

export type RentalChartWindow = {
  revenueSatang: number;
  rentalCount: number;
  paidSatang?: number;
  refundedSatang?: number;
};

export type RentalChartComparison = { revenueSatang: number; count: number };

export function percentageHint(current: number, previous: number) {
  if (previous <= 0) return "ยังไม่มีข้อมูลเปรียบเทียบ";
  const change = ((current - previous) / previous) * 100;
  return `${change >= 0 ? "+" : ""}${change.toFixed(1)}% เทียบช่วงก่อนหน้า`;
}

function revenueChartData(chart: RentalChartPoint[]) {
  return chart.map((point) => ({
    ...point,
    revenueBaht: point.revenueSatang / 100,
  }));
}

function hasChartValues(chart: RentalChartPoint[]) {
  return chart.some((point) => point.revenueSatang > 0 || point.count > 0);
}

export function RentalCharts({
  chart,
  chartPrevious,
  current,
  previous,
}: {
  chart: RentalChartPoint[];
  chartPrevious: RentalChartComparison;
  current: RentalChartWindow;
  previous: RentalChartWindow;
}) {
  const data = revenueChartData(chart);
  const populated = hasChartValues(chart);
  return (
    <div className="grid min-w-0 items-start gap-4 xl:grid-cols-2">
      <Card className="gap-3 rounded-2xl border shadow-none">
        <CardHeader>
          <CardTitle>รายได้จากการเช่าตามช่วงเวลา</CardTitle>
          <CardDescription>รายได้สุทธิจากรายการที่ชำระแล้ว · เวลาไทย</CardDescription>
          <p className="text-xs text-muted-foreground">
            ช่วงก่อนหน้า: {formatBalance(chartPrevious.revenueSatang)} ·{" "}
            {percentageHint(current.revenueSatang, previous.revenueSatang)}
          </p>
        </CardHeader>
        <CardContent>
          {populated ? (
            <ChartContainer
              config={revenueConfig}
              className="h-60 w-full aspect-auto"
              initialDimension={{ width: 600, height: 240 }}
              role="region"
              aria-label="กราฟรายได้จากการเช่า"
            >
              <BarChart accessibilityLayer data={data} margin={{ top: 8, right: 8, bottom: 4, left: 0 }}>
                <CartesianGrid vertical={false} strokeDasharray="3 4" />
                <XAxis dataKey="label" tickLine={false} axisLine={false} tickMargin={10} minTickGap={16} tick={{ fontSize: 11, fill: "var(--muted-foreground)" }} />
                <YAxis tickLine={false} axisLine={false} tickMargin={8} tick={{ fontSize: 11, fill: "var(--muted-foreground)" }} tickFormatter={(value) => `฿${Number(value).toLocaleString("th-TH")}`} />
                <ChartTooltip content={<ChartTooltipContent />} />
                <Bar dataKey="revenueBaht" fill="var(--color-revenueBaht)" radius={[4, 4, 0, 0]} isAnimationActive={false} />
              </BarChart>
            </ChartContainer>
          ) : (
            <p className="flex h-60 items-center justify-center rounded-2xl border border-dashed px-6 text-center text-sm text-muted-foreground">
              ยังไม่มีข้อมูลรายได้ในช่วงที่เลือก
            </p>
          )}
        </CardContent>
      </Card>
      <Card className="gap-3 rounded-2xl border shadow-none">
        <CardHeader>
          <CardTitle>จำนวนรายการเช่าตามช่วงเวลา</CardTitle>
          <CardDescription>รายการที่ชำระแล้วจากฐานข้อมูล</CardDescription>
          <p className="text-xs text-muted-foreground">
            ช่วงก่อนหน้า: {chartPrevious.count.toLocaleString("th-TH")} รายการ ·{" "}
            {percentageHint(current.rentalCount, previous.rentalCount)}
          </p>
        </CardHeader>
        <CardContent>
          {populated ? (
            <ChartContainer
              config={countConfig}
              className="h-60 w-full aspect-auto"
              initialDimension={{ width: 600, height: 240 }}
              role="region"
              aria-label="กราฟจำนวนรายการเช่า"
            >
              <BarChart accessibilityLayer data={data} margin={{ top: 8, right: 8, bottom: 4, left: -16 }}>
                <CartesianGrid vertical={false} strokeDasharray="3 4" />
                <XAxis dataKey="label" tickLine={false} axisLine={false} tickMargin={10} minTickGap={16} tick={{ fontSize: 11, fill: "var(--muted-foreground)" }} />
                <YAxis allowDecimals={false} tickLine={false} axisLine={false} tickMargin={8} tick={{ fontSize: 11, fill: "var(--muted-foreground)" }} />
                <ChartTooltip content={<ChartTooltipContent />} />
                <Bar dataKey="count" fill="var(--color-count)" radius={[4, 4, 0, 0]} isAnimationActive={false} />
              </BarChart>
            </ChartContainer>
          ) : (
            <p className="flex h-60 items-center justify-center rounded-2xl border border-dashed px-6 text-center text-sm text-muted-foreground">
              ยังไม่มีข้อมูลรายการในช่วงที่เลือก
            </p>
          )}
        </CardContent>
      </Card>
    </div>
  );
}
