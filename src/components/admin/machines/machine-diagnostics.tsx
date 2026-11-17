"use client";

import { Activity } from "lucide-react";
import { Badge } from "@/components/ui/badge";
import {
  READINESS_CHECK_LABELS,
  READINESS_STATUS_LABELS,
  readinessStatusTone,
} from "@/lib/machine-display";
import type { Machine } from "@/lib/machine-contracts";

export function MachineDiagnostics({ machine }: { machine: Machine }) {
  const diagnostics = machine.readinessDiagnostics;
  return (
    <div className="flex flex-col gap-4">
      <div className="flex items-start gap-3">
        <span className="flex size-9 shrink-0 items-center justify-center rounded-xl bg-muted text-muted-foreground">
          <Activity aria-hidden className="size-4" />
        </span>
        <div className="flex flex-col gap-1">
          <h3 className="font-heading text-base font-medium">ผลตรวจวินิจฉัยจาก Agent</h3>
          <p className="text-sm text-muted-foreground">
            ผลตรวจวินิจฉัยจาก Agent เท่านั้น ไม่ใช่การอนุมัติให้เช่า
          </p>
        </div>
      </div>
      {diagnostics?.length ? (
        <ul aria-label="รายการผลตรวจวินิจฉัย" className="divide-y rounded-2xl border">
          {diagnostics.map((diagnostic) => (
            <li
              key={diagnostic.checkId}
              className="flex flex-wrap items-center justify-between gap-2 px-4 py-2.5 text-sm"
            >
              <span>{READINESS_CHECK_LABELS[diagnostic.checkId] ?? diagnostic.checkId}</span>
              <Badge variant={readinessStatusTone(diagnostic.status)}>
                {READINESS_STATUS_LABELS[diagnostic.status] ?? diagnostic.status}
              </Badge>
            </li>
          ))}
        </ul>
      ) : diagnostics ? (
        <p className="rounded-2xl border border-dashed px-4 py-6 text-center text-sm text-muted-foreground">
          รายงานนี้ไม่มีรายการตรวจวินิจฉัย
        </p>
      ) : (
        <p className="rounded-2xl border border-dashed px-4 py-6 text-center text-sm text-muted-foreground">
          Agent ยังไม่รายงานผลตรวจวินิจฉัย
        </p>
      )}
    </div>
  );
}
