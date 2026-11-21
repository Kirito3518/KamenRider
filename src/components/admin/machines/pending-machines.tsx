"use client";

import { useState } from "react";
import { ChevronDown, Monitor, Settings2, ShieldAlert, ShieldCheck } from "lucide-react";
import { AdminCard } from "@/components/admin/admin-page";
import { Badge } from "@/components/ui/badge";
import { Button } from "@/components/ui/button";
import { Collapsible, CollapsibleContent, CollapsibleTrigger } from "@/components/ui/collapsible";
import {
  Empty,
  EmptyDescription,
  EmptyHeader,
  EmptyMedia,
  EmptyTitle,
} from "@/components/ui/empty";
import { Skeleton } from "@/components/ui/skeleton";
import { isPendingMachine } from "@/lib/machine-display";
import type { Machine } from "@/lib/machine-contracts";

export function PendingMachines({
  machines,
  onOpenSettings,
  onApprove,
}: {
  machines: Machine[] | null;
  onOpenSettings: (machineId: string) => void;
  onApprove: (machineId: string) => void;
}) {
  const [open, setOpen] = useState<boolean | null>(null);
  const loading = machines === null;
  const pending = machines?.filter(isPendingMachine) ?? [];
  const expanded = open ?? pending.length > 0;

  return (
    <AdminCard className="gap-0 py-0">
      <Collapsible open={expanded} onOpenChange={setOpen}>
        <CollapsibleTrigger
          aria-label={`เครื่องที่รออนุมัติ ${pending.length} เครื่อง`}
          className="group flex w-full items-start justify-between gap-4 rounded-2xl px-(--card-spacing) py-(--card-spacing) text-left outline-none focus-visible:ring-3 focus-visible:ring-ring/30"
        >
          <span className="flex min-w-0 items-start gap-3">
            <span className="flex size-9 shrink-0 items-center justify-center rounded-xl bg-status-busy/10 text-status-busy">
              <ShieldAlert aria-hidden className="size-4" />
            </span>
            <span className="flex min-w-0 flex-col gap-1">
              <span className="flex flex-wrap items-center gap-2">
                <span className="font-heading text-base font-medium">เครื่องที่รออนุมัติ</span>
                {loading ? <Skeleton className="h-5 w-8 rounded-3xl" /> : <Badge variant="busy">{pending.length}</Badge>}
              </span>
              <span className="text-sm text-muted-foreground">
                กดอนุมัติเพื่อตั้งค่าเครื่องก่อน แล้วยืนยันอีกครั้ง — ไม่ต้องกรอก fingerprint
              </span>
            </span>
          </span>
          <ChevronDown
            aria-hidden
            className="mt-1 size-4 shrink-0 text-muted-foreground transition-transform group-data-panel-open:rotate-180"
          />
        </CollapsibleTrigger>
        <CollapsibleContent className="data-open:animate-in data-open:fade-in-0 data-open:slide-in-from-top-1">
          <div className="flex flex-col gap-2 px-(--card-spacing) pb-(--card-spacing)">
            {loading ? (
              <>
                <Skeleton className="h-16 w-full rounded-2xl" />
                <Skeleton className="h-16 w-full rounded-2xl" />
              </>
            ) : pending.length === 0 ? (
              <Empty className="border border-dashed py-8">
                <EmptyHeader>
                  <EmptyMedia variant="icon">
                    <ShieldCheck aria-hidden />
                  </EmptyMedia>
                  <EmptyTitle>ไม่มีเครื่องที่รออนุมัติ</EmptyTitle>
                  <EmptyDescription>
                    เครื่องที่ลงทะเบียนใหม่จะแสดงที่นี่เพื่อตั้งค่าและอนุมัติ
                  </EmptyDescription>
                </EmptyHeader>
              </Empty>
            ) : (
              <ul aria-label="รายการเครื่องที่รออนุมัติ" className="flex flex-col gap-2">
                {pending.map((machine) => (
                  <li
                    key={machine.machineId}
                    className="flex flex-wrap items-center gap-3 rounded-2xl bg-muted/50 p-3"
                  >
                    <span className="flex size-9 shrink-0 items-center justify-center rounded-xl bg-background text-muted-foreground">
                      <Monitor aria-hidden className="size-4" />
                    </span>
                    <div className="flex min-w-0 flex-1 flex-col">
                      <p className="truncate font-medium">{machine.displayName || machine.machineId}</p>
                      <p className="truncate font-mono text-xs text-muted-foreground">
                        {machine.machineId}
                        {machine.hostname ? ` · ${machine.hostname}` : ""}
                      </p>
                    </div>
                    <Badge variant="busy">รออนุมัติ</Badge>
                    <Button
                      type="button"
                      variant="ghost"
                      size="icon-sm"
                      aria-label={`ตั้งค่าเครื่อง ${machine.machineId}`}
                      onClick={() => onOpenSettings(machine.machineId)}
                    >
                      <Settings2 aria-hidden />
                    </Button>
                    <Button
                      type="button"
                      variant="success"
                      size="sm"
                      aria-label={`อนุมัติเครื่อง ${machine.machineId}`}
                      title={machine.publicKeyFingerprint ? undefined : "Agent ยังไม่รายงาน fingerprint"}
                      disabled={!machine.publicKeyFingerprint}
                      onClick={() => onApprove(machine.machineId)}
                    >
                      <ShieldCheck data-icon="inline-start" />
                      อนุมัติ
                    </Button>
                  </li>
                ))}
              </ul>
            )}
          </div>
        </CollapsibleContent>
      </Collapsible>
    </AdminCard>
  );
}
