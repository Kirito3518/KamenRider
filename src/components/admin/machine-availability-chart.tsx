"use client";

import { CircleAlert } from "lucide-react";
import { Cell, Pie, PieChart } from "recharts";
import { AdminCard } from "@/components/admin/admin-page";
import {
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
import {
  MACHINE_AVAILABILITY_SEGMENT_META,
  machineAvailabilitySegments,
  type MachineSummary,
} from "@/lib/machine-summary";

/**
 * Donut of the availability breakdown for approved machines. Only summarized
 * counts cross the server/client boundary; no raw Controller machine rows (with
 * private IPs, ports or fingerprints) are ever passed to the browser.
 *
 * The chart is deliberately non-animated so the server-rendered overview does
 * not move after hydration.
 */

const chartConfig = Object.fromEntries(
  Object.entries(MACHINE_AVAILABILITY_SEGMENT_META).map(([key, meta]) => [
    key,
    { label: meta.label, color: meta.color },
  ]),
) satisfies ChartConfig;

function availabilitySummaryText(summary: MachineSummary) {
  const segments = machineAvailabilitySegments(summary).filter((segment) => segment.value > 0);
  const parts = segments.map((segment) => `${segment.label} ${segment.value} เครื่อง`);
  const pending = summary.pendingApproval
    ? ` · รออนุมัติ ${summary.pendingApproval} เครื่อง`
    : "";
  return `สรุปสถานะเครื่อง: ${parts.length ? parts.join(" · ") : "ยังไม่มีเครื่องที่อนุมัติแล้ว"} · รวม ${summary.approved} เครื่อง${pending}`;
}

function segmentTooltipFormatter(value: unknown) {
  const numeric = Number(value);
  return (
    <span className="font-mono font-medium text-foreground tabular-nums">
      {Number.isFinite(numeric) ? `${numeric.toLocaleString("th-TH")} เครื่อง` : "—"}
    </span>
  );
}

export function MachineAvailabilityChart({
  summary,
  available,
}: {
  summary: MachineSummary;
  available: boolean;
}) {
  const segments = machineAvailabilitySegments(summary);
  const visibleSegments = segments.filter((segment) => segment.value > 0);
  return (
    <AdminCard className="h-full gap-3">
      <CardHeader className="gap-1">
        <CardTitle>สัดส่วนสถานะเครื่อง</CardTitle>
        <CardDescription>
          เครื่องที่อนุมัติแล้ว แยกตามสถานะจาก Controller · ไม่นับเครื่องรออนุมัติ
        </CardDescription>
      </CardHeader>
      <CardContent>
        {!available ? (
          <div
            role="status"
            className="flex h-64 flex-col items-center justify-center gap-2 rounded-2xl border border-dashed px-6 text-center"
          >
            <CircleAlert aria-hidden className="size-5 text-status-maintenance" />
            <p className="font-medium">ติดต่อระบบเครื่องไม่ได้</p>
            <p className="text-sm text-muted-foreground">
              ไม่สามารถแสดงสถานะเครื่องจาก Controller ได้ในขณะนี้ · ข้อมูลการเงินยังแสดงตามปกติ
            </p>
          </div>
        ) : summary.approved === 0 ? (
          <>
            <p className="flex h-64 items-center justify-center rounded-2xl border border-dashed px-6 text-center text-sm text-muted-foreground">
              ยังไม่มีเครื่องที่อนุมัติให้เช่า
            </p>
            <p className="sr-only">
              {availabilitySummaryText(summary)}
            </p>
          </>
        ) : (
          <>
            <div className="relative">
              <ChartContainer
                config={chartConfig}
                className="mx-auto h-64 w-full max-w-sm aspect-auto"
                initialDimension={{ width: 480, height: 256 }}
                role="region"
                aria-label="กราฟสัดส่วนสถานะเครื่อง"
              >
                <PieChart accessibilityLayer>
                  <ChartTooltip
                    content={
                      <ChartTooltipContent nameKey="key" formatter={segmentTooltipFormatter} />
                    }
                  />
                  <Pie
                    data={visibleSegments}
                    dataKey="value"
                    nameKey="key"
                    innerRadius="58%"
                    outerRadius="86%"
                    paddingAngle={1}
                    strokeWidth={0}
                    isAnimationActive={false}
                  >
                    {visibleSegments.map((segment) => (
                      <Cell key={segment.key} fill={segment.color} />
                    ))}
                  </Pie>
                </PieChart>
              </ChartContainer>
              <div
                aria-hidden="true"
                className="pointer-events-none absolute inset-0 flex flex-col items-center justify-center"
              >
                <span className="text-2xl font-semibold tabular-nums">
                  {summary.approved.toLocaleString("th-TH")}
                </span>
                <span className="text-xs text-muted-foreground">เครื่องที่อนุมัติแล้ว</span>
              </div>
            </div>
            <ul className="mt-2 flex flex-wrap items-center justify-center gap-x-4 gap-y-2 text-xs">
              {visibleSegments.map((segment) => (
                <li key={segment.key} className="flex items-center gap-1.5">
                  <span
                    aria-hidden="true"
                    className="size-2 shrink-0 rounded-[2px]"
                    style={{ backgroundColor: segment.color }}
                  />
                  <span className="text-muted-foreground">{segment.label}</span>
                  <span className="font-medium tabular-nums">{segment.value.toLocaleString("th-TH")}</span>
                </li>
              ))}
            </ul>
            <p className="mt-2 text-center text-xs text-muted-foreground">
              {summary.pendingApproval
                ? `รออนุมัติ ${summary.pendingApproval.toLocaleString("th-TH")} เครื่อง (ไม่รวมในสัดส่วน)`
                : "ไม่มีเครื่องรออนุมัติ"}
            </p>
            <p className="sr-only">{availabilitySummaryText(summary)}</p>
          </>
        )}
      </CardContent>
    </AdminCard>
  );
}
