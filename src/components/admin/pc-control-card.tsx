"use client";

import { Clock, KeyRound, Minus, Monitor, Plus, Power, PowerOff, UserRound, XCircle } from "lucide-react";
import { Badge } from "@/components/ui/badge";
import { Button } from "@/components/ui/button";
import { Card, CardAction, CardContent, CardDescription, CardHeader, CardTitle } from "@/components/ui/card";
import { Empty, EmptyContent, EmptyDescription, EmptyHeader, EmptyMedia, EmptyTitle } from "@/components/ui/empty";
import { PcControlVnc } from "@/components/admin/pc-control-vnc";
import {
  agentOnline,
  formatPcControlDateTime,
  pcControlStatusLabels,
  remainingLabel,
  type PcControlMachine,
} from "@/lib/pc-control";

const activeUpdateStatuses = new Set(["awaitingInstaller", "installing", "awaitingHeartbeat", "rollingBack"]);
const terminalLeaseStates = new Set(["ending", "failed", "completed"]);

export function PcControlCard({
  machine,
  now,
  busy,
  vncOpen,
  onWake,
  onOpenVnc,
  onCloseVnc,
  onPair,
  onTime,
  onAddTime,
  onReduceTime,
  onPowerOff,
  onCancel,
}: {
  machine: PcControlMachine;
  now: number;
  busy: boolean;
  vncOpen: boolean;
  onWake: () => void;
  onOpenVnc: () => void;
  onCloseVnc: () => void;
  onPair: () => void;
  onTime: () => void;
  onAddTime: () => void;
  onReduceTime: () => void;
  onPowerOff: () => void;
  onCancel: () => void;
}) {
  const lease = machine.lease;
  const online = agentOnline(machine, now);
  const updateLocked = Boolean(machine.agentUpdateStatus && activeUpdateStatuses.has(machine.agentUpdateStatus));
  const leaseActionable =
    lease !== null && lease.state === "active" && lease.pendingRevision === null && !terminalLeaseStates.has(lease.state);
  const remaining = lease?.remainingSeconds ?? null;

  return (
    <Card size="sm" className="rounded-2xl border shadow-none ring-0">
      <CardHeader>
        <CardTitle className="truncate">{machine.displayName || machine.machineId}</CardTitle>
        <CardDescription className="truncate font-mono text-xs">{machine.machineId}</CardDescription>
        <CardAction className="flex flex-wrap justify-end gap-1.5">
          <Badge variant={lease ? "busy" : online ? "available" : "maintenance"}>
            {lease ? pcControlStatusLabels[lease.state] || lease.state : online ? "ออนไลน์" : "ออฟไลน์"}
          </Badge>
          {machine.approvalStatus !== "approved" ? (
            <Badge variant="outline">{machine.approvalStatus === "pending" ? "รออนุมัติ" : machine.approvalStatus}</Badge>
          ) : null}
          {updateLocked ? <Badge variant="maintenance">Agent update</Badge> : null}
        </CardAction>
      </CardHeader>
      <CardContent className="flex flex-col gap-4">
        {vncOpen ? (
          <PcControlVnc machineId={machine.machineId} onClose={onCloseVnc} />
        ) : (
          <Empty className="border border-dashed py-8">
            <EmptyHeader>
              <EmptyMedia variant="icon">
                <Monitor aria-hidden />
              </EmptyMedia>
              <EmptyTitle>จอ VNC ปิดอยู่</EmptyTitle>
              <EmptyDescription>เปิดดูหน้าจอเมื่อต้องการ ไม่มีการสตรีมตลอดเวลา</EmptyDescription>
            </EmptyHeader>
            <EmptyContent>
              <Button variant="outline" size="sm" onClick={onOpenVnc} disabled={busy || !machine.localIp}>
                <Monitor data-icon="inline-start" />
                เปิดจอ
              </Button>
            </EmptyContent>
          </Empty>
        )}

        <div className="flex flex-col gap-2 rounded-2xl bg-muted/50 p-3 text-sm">
          {lease ? (
            <>
              <div className="flex flex-wrap items-center gap-2">
                <UserRound aria-hidden className="size-4 text-muted-foreground" />
                <span className="font-medium">{lease.userName}</span>
                <span className="truncate text-xs text-muted-foreground">{lease.userEmail || lease.userId}</span>
                {lease.pendingRevision !== null ? <Badge variant="busy">รอ Agent ยืนยัน</Badge> : null}
              </div>
              <div className="flex flex-wrap items-center gap-2 text-muted-foreground">
                <Clock aria-hidden className="size-4" />
                <span className="font-mono tabular-nums text-foreground">{remainingLabel(remaining)}</span>
                <span>คงเหลือ</span>
                {lease.expiresAtUtc ? <span>· หมดอายุ {formatPcControlDateTime(lease.expiresAtUtc)}</span> : null}
              </div>
            </>
          ) : (
            <p className="text-muted-foreground">ยังไม่มีผู้เช่า · เครื่อง{' '}
              {machine.canRent ? "พร้อมให้เช่า" : "ยังไม่พร้อมให้เช่า"}</p>
          )}
        </div>

        <div className="flex flex-wrap gap-2">
          <Button variant="outline" size="sm" onClick={onWake} disabled={busy || online}>
            <Power data-icon="inline-start" />
            เปิดเครื่อง
          </Button>
          <Button variant="destructive" size="sm" onClick={onPowerOff} disabled={busy || !online}>
            <PowerOff data-icon="inline-start" />
            ปิดเครื่อง
          </Button>
          <Button variant="outline" size="sm" onClick={onPair} disabled={busy || !leaseActionable}>
            <KeyRound data-icon="inline-start" />
            เชื่อมต่อพิน
          </Button>
          <Button variant="outline" size="sm" onClick={onAddTime} disabled={busy || !leaseActionable}>
            <Plus data-icon="inline-start" />
            1 ชม.
          </Button>
          <Button variant="outline" size="sm" onClick={onReduceTime} disabled={busy || !leaseActionable}>
            <Minus data-icon="inline-start" />
            1 ชม.
          </Button>
          <Button variant="outline" size="sm" onClick={onTime} disabled={busy || !leaseActionable}>
            <Clock data-icon="inline-start" />
            กำหนดเวลา
          </Button>
          <Button variant="secondary" size="sm" onClick={onCancel} disabled={busy || !leaseActionable}>
            <XCircle data-icon="inline-start" />
            ยกเลิกการเช่า
          </Button>
        </div>
      </CardContent>
    </Card>
  );
}
