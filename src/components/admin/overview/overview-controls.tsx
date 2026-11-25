"use client";

import { useTransition } from "react";
import { useRouter } from "next/navigation";
import { RefreshCw } from "lucide-react";
import { Button } from "@/components/ui/button";
import {
  HistoryRangePicker,
  type HistoryRangePeriod,
} from "@/components/history/history-range-picker";

const OVERVIEW_PERIODS: HistoryRangePeriod[] = ["today", "7d", "30d", "90d", "custom"];

/**
 * Period picker + refresh control for the admin overview. Changing the period
 * pushes the dashboard search params; "รีเฟรช" re-runs every server section.
 * The initial period comes from the server so the picker never disagrees with
 * the data that was actually loaded.
 */
export function OverviewControls({
  period,
  from,
  to,
}: {
  period: string;
  from: string;
  to: string;
}) {
  const router = useRouter();
  const [pending, startTransition] = useTransition();
  const currentPeriod: HistoryRangePeriod = OVERVIEW_PERIODS.includes(
    period as HistoryRangePeriod,
  )
    ? (period as HistoryRangePeriod)
    : "30d";

  function navigate(next: { period: HistoryRangePeriod; from: string; to: string }) {
    const params = new URLSearchParams();
    params.set("period", next.period);
    if (next.period === "custom") {
      if (next.from) params.set("from", next.from);
      if (next.to) params.set("to", next.to);
    }
    startTransition(() => {
      router.replace(`/admin/overview?${params.toString()}`, { scroll: false });
    });
  }

  return (
    <div className="flex w-full flex-col gap-2 xl:w-auto xl:items-end" aria-busy={pending}>
      <div className="flex justify-end">
        <Button
          type="button"
          variant="outline"
          size="sm"
          disabled={pending}
          onClick={() => startTransition(() => router.refresh())}
        >
          <RefreshCw data-icon="inline-start" aria-hidden />
          รีเฟรช
        </Button>
      </div>
      <HistoryRangePicker
        period={currentPeriod}
        from={from}
        to={to}
        disabled={pending}
        onChange={navigate}
      />
    </div>
  );
}
