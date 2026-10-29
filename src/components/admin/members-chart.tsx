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

const chartConfig = {
  count: { label: "สมัครใหม่ (บัญชี)", color: "var(--primary)" },
} satisfies ChartConfig;

export function MembersChart({
  data,
  from,
  to,
}: {
  data: { key: string; label: string; count: number }[];
  from: string;
  to: string;
}) {
  const maximum = Math.max(
    4,
    Math.ceil(Math.max(...data.map((point) => point.count), 0) / 4) * 4,
  );
  return (
    <Card className="gap-3 rounded-2xl border shadow-none">
      <CardHeader className="gap-1">
        <CardTitle>การสมัครใหม่ตามช่วงเวลา</CardTitle>
        <CardDescription>
          {from} ถึง {to} · เวลาไทย
        </CardDescription>
      </CardHeader>
      <CardContent>
        <ChartContainer
          config={chartConfig}
          className="h-60 w-full aspect-auto"
          role="region"
          aria-label="กราฟจำนวนสมาชิกที่สมัครใหม่ตามช่วงเวลาที่เลือก"
        >
          <BarChart
            accessibilityLayer
            data={data}
            margin={{ top: 8, right: 8, bottom: 4, left: -16 }}
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
              allowDecimals={false}
              domain={[0, maximum]}
              ticks={[0, maximum / 4, maximum / 2, (maximum * 3) / 4, maximum]}
              tickLine={false}
              axisLine={false}
              tickMargin={8}
              tick={{ fontSize: 11, fill: "var(--muted-foreground)" }}
            />
            <ChartTooltip cursor={false} content={<ChartTooltipContent />} />
            <Bar
              dataKey="count"
              fill="var(--color-count)"
              radius={[4, 4, 0, 0]}
              isAnimationActive={false}
            />
          </BarChart>
        </ChartContainer>
        <p className="sr-only">
          {data
            .map((point) => `${point.label}: ${point.count} บัญชี`)
            .join("; ")}
        </p>
        {!data.some((point) => point.count) && (
          <p className="mt-2 text-center text-xs text-muted-foreground">
            ยังไม่มีการสมัครใหม่ในช่วงที่เลือก
          </p>
        )}
      </CardContent>
    </Card>
  );
}
