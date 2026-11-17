"use client";

import Link from "next/link";
import { Monitor, Settings2, ServerOff } from "lucide-react";
import { AdminCard } from "@/components/admin/admin-page";
import { Badge } from "@/components/ui/badge";
import { Button, buttonVariants } from "@/components/ui/button";
import { CardContent, CardDescription, CardHeader, CardTitle } from "@/components/ui/card";
import {
  Empty,
  EmptyContent,
  EmptyDescription,
  EmptyHeader,
  EmptyMedia,
  EmptyTitle,
} from "@/components/ui/empty";
import { Skeleton } from "@/components/ui/skeleton";
import { Table, TableBody, TableCell, TableHead, TableHeader, TableRow } from "@/components/ui/table";
import { isAgentOnline, updateStatusLabels } from "@/lib/agent-releases";
import {
  agentUpdateTone,
  isApprovedMachine,
  rentabilityLabel,
  rentabilityTone,
} from "@/lib/machine-display";
import type { Machine } from "@/lib/machine-contracts";
import { formatRamBytes, formatSpec } from "@/lib/machine-specs";
import { formatBalance } from "@/lib/money";
import { cn } from "@/lib/utils";

export function ApprovedMachinesTable({
  machines,
  now,
  onOpenSettings,
  onOpenInstall,
}: {
  machines: Machine[] | null;
  now: number;
  onOpenSettings: (machineId: string) => void;
  onOpenInstall: () => void;
}) {
  const loading = machines === null;
  const approved = machines?.filter(isApprovedMachine) ?? [];

  return (
    <AdminCard>
      <CardHeader>
        <CardTitle>เครื่องที่อนุมัติแล้ว</CardTitle>
        <CardDescription>
          จัดการชื่อ ราคา พอร์ต และ Agent update ของเครื่องที่พร้อมให้บริการ · การควบคุมเครื่องและ VNC อยู่ที่ PC Control
        </CardDescription>
      </CardHeader>
      <CardContent>
        {loading ? (
          <div className="flex flex-col gap-2" aria-hidden>
            {Array.from({ length: 4 }, (_, index) => (
              <Skeleton key={index} className="h-14 w-full rounded-2xl" />
            ))}
          </div>
        ) : approved.length === 0 ? (
          <Empty className="border border-dashed">
            <EmptyHeader>
              <EmptyMedia variant="icon">
                <ServerOff aria-hidden />
              </EmptyMedia>
              <EmptyTitle>ยังไม่มีเครื่องที่อนุมัติแล้ว</EmptyTitle>
              <EmptyDescription>
                ติดตั้ง Agent บนเครื่องลูก แล้วอนุมัติเครื่องที่รายการด้านบนเพื่อเริ่มให้บริการ
              </EmptyDescription>
            </EmptyHeader>
            <EmptyContent>
              <Button type="button" size="sm" onClick={onOpenInstall}>
                <Monitor data-icon="inline-start" />
                ติดตั้ง Agent เครื่องแรก
              </Button>
            </EmptyContent>
          </Empty>
        ) : (
          <Table>
            <caption className="sr-only">เครื่องที่อนุมัติแล้ว</caption>
            <TableHeader>
              <TableRow>
                <TableHead>เครื่อง</TableHead>
                <TableHead>สถานะ</TableHead>
                <TableHead>สเปก</TableHead>
                <TableHead>ราคา/ชม.</TableHead>
                <TableHead>Agent</TableHead>
                <TableHead className="text-right">จัดการ</TableHead>
              </TableRow>
            </TableHeader>
            <TableBody>
              {approved.map((machine) => {
                const online = isAgentOnline(machine, now);
                return (
                  <TableRow key={machine.machineId}>
                    <TableCell>
                      <div className="flex flex-col">
                        <span className="font-medium">{machine.displayName || machine.machineId}</span>
                        <span className="font-mono text-xs text-muted-foreground">{machine.machineId}</span>
                      </div>
                    </TableCell>
                    <TableCell>
                      <div className="flex flex-col items-start gap-1">
                        <span className="flex items-center gap-2">
                          <span
                            aria-hidden
                            className={cn(
                              "size-2 rounded-full",
                              online ? "bg-status-available" : "bg-status-maintenance",
                            )}
                          />
                          {online ? "ออนไลน์" : "ออฟไลน์"}
                        </span>
                        <Badge variant={rentabilityTone(machine)}>{rentabilityLabel(machine)}</Badge>
                      </div>
                    </TableCell>
                    <TableCell className="max-w-56 whitespace-normal">
                      <div className="flex flex-col gap-0.5 text-xs text-muted-foreground">
                        <span className="truncate" title={machine.cpu ?? undefined}>{`CPU: ${formatSpec(machine.cpu)}`}</span>
                        <span className="truncate" title={machine.gpu ?? undefined}>{`GPU: ${formatSpec(machine.gpu)}`}</span>
                        <span>{`RAM: ${formatRamBytes(machine.ramBytes)}`}</span>
                      </div>
                    </TableCell>
                    <TableCell>
                      <div className="flex flex-col">
                        <span className="tabular-nums">{formatBalance(machine.hourlyPriceMinorUnits)}</span>
                        <span className="text-xs text-muted-foreground">ต่อชั่วโมง</span>
                      </div>
                    </TableCell>
                    <TableCell>
                      <div className="flex flex-col items-start gap-1">
                        <span className="font-mono text-xs">{machine.agentVersion || "ยังไม่รายงาน"}</span>
                        {machine.agentUpdateStatus ? (
                          <Badge variant={agentUpdateTone(machine.agentUpdateStatus)}>
                            {updateStatusLabels[machine.agentUpdateStatus] || machine.agentUpdateStatus}
                          </Badge>
                        ) : (
                          <span className="text-xs text-muted-foreground">ไม่มีการอัปเดต</span>
                        )}
                      </div>
                    </TableCell>
                    <TableCell className="text-right">
                      <div className="flex justify-end gap-1.5">
                        <Button
                          type="button"
                          variant="secondary"
                          size="sm"
                          aria-label={`ตั้งค่าเครื่อง ${machine.machineId}`}
                          onClick={() => onOpenSettings(machine.machineId)}
                        >
                          <Settings2 data-icon="inline-start" />
                          ตั้งค่า
                        </Button>
                        <Link
                          href="/admin/pc-control"
                          aria-label={`ควบคุมเครื่อง ${machine.machineId}`}
                          className={cn(buttonVariants({ variant: "outline", size: "sm" }))}
                        >
                          <Monitor data-icon="inline-start" />
                          ควบคุม
                        </Link>
                      </div>
                    </TableCell>
                  </TableRow>
                );
              })}
            </TableBody>
          </Table>
        )}
      </CardContent>
    </AdminCard>
  );
}
