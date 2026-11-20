"use client";

import { useState } from "react";
import { Button } from "@/components/ui/button";
import { Field, FieldGroup, FieldLabel } from "@/components/ui/field";
import { Input } from "@/components/ui/input";
import { ToggleGroup, ToggleGroupItem } from "@/components/ui/toggle-group";

export type HistoryRangePeriod =
  | "all"
  | "today"
  | "7d"
  | "30d"
  | "90d"
  | "custom";

const OPTIONS: { value: HistoryRangePeriod; label: string }[] = [
  { value: "all", label: "ทั้งหมด" },
  { value: "today", label: "วันนี้" },
  { value: "7d", label: "7 วัน" },
  { value: "30d", label: "30 วัน" },
  { value: "90d", label: "90 วัน" },
  { value: "custom", label: "กำหนดเอง" },
];

export function HistoryRangePicker({
  period,
  from,
  to,
  allowAll = false,
  disabled = false,
  onChange,
}: {
  period: HistoryRangePeriod;
  from: string;
  to: string;
  allowAll?: boolean;
  disabled?: boolean;
  onChange: (value: { period: HistoryRangePeriod; from: string; to: string }) => void;
}) {
  const [customFrom, setCustomFrom] = useState(from);
  const [customTo, setCustomTo] = useState(to);
  const options = allowAll ? OPTIONS : OPTIONS.filter((option) => option.value !== "all");

  return (
    <div className="flex flex-col gap-2">
      <div className="max-w-full overflow-x-auto">
        <ToggleGroup
          value={[period]}
          onValueChange={(values) => {
            const next = values[0] as HistoryRangePeriod | undefined;
            if (!next) return;
            if (next === "custom") {
              const today = new Date(Date.now() + 7 * 60 * 60 * 1_000)
                .toISOString()
                .slice(0, 10);
              onChange({
                period: "custom",
                from: customFrom || from || today,
                to: customTo || to || today,
              });
            } else {
              onChange({ period: next, from: "", to: "" });
            }
          }}
          aria-label="ช่วงเวลาของประวัติ"
          disabled={disabled}
          spacing={1}
          className="rounded-xl border bg-muted/50 p-1"
        >
          {options.map((option) => (
            <ToggleGroupItem
              key={option.value}
              value={option.value}
              size="sm"
              className="rounded-lg px-3 text-xs data-pressed:bg-primary data-pressed:text-primary-foreground"
            >
              {option.label}
            </ToggleGroupItem>
          ))}
        </ToggleGroup>
      </div>
      {period === "custom" ? (
        <form
          className="flex flex-wrap items-end gap-2"
          onSubmit={(event) => {
            event.preventDefault();
            onChange({ period: "custom", from: customFrom, to: customTo });
          }}
        >
          <FieldGroup className="grid gap-2 sm:grid-cols-[1fr_1fr_auto] sm:items-end">
            <Field>
              <FieldLabel htmlFor="history-from" className="text-xs">
                ตั้งแต่วันที่
              </FieldLabel>
              <Input
                id="history-from"
                type="date"
                value={customFrom}
                disabled={disabled}
                onChange={(event) => setCustomFrom(event.currentTarget.value)}
                className="w-full"
              />
            </Field>
            <Field>
              <FieldLabel htmlFor="history-to" className="text-xs">
                ถึงวันที่
              </FieldLabel>
              <Input
                id="history-to"
                type="date"
                value={customTo}
                disabled={disabled}
                onChange={(event) => setCustomTo(event.currentTarget.value)}
                className="w-full"
              />
            </Field>
            <Button type="submit" size="sm" disabled={disabled}>
              ใช้ช่วงเวลา
            </Button>
          </FieldGroup>
        </form>
      ) : null}
    </div>
  );
}
