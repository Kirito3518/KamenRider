"use client";

import type { LucideIcon } from "lucide-react";
import type { ReactNode } from "react";
import {
  Clock,
  KeyRound,
  Minus,
  Monitor,
  Plus,
  Power,
  PowerOff,
  RefreshCw,
  Unlink,
  UserRound,
  XCircle,
} from "lucide-react";
import { Badge } from "@/components/ui/badge";
import { Button } from "@/components/ui/button";
import { Card, CardAction, CardContent, CardDescription, CardHeader, CardTitle } from "@/components/ui/card";
import { Empty, EmptyContent, EmptyDescription, EmptyHeader, EmptyMedia, EmptyTitle } from "@/components/ui/empty";
import { PcControlVnc } from "@/components/admin/pc-control-vnc";
import { cn } from "@/lib/utils";
import {
  agentOnline,
  formatPcControlDateTime,
  pcControlStatusLabels,
  remainingLabel,
  type PcControlMachine,
} from "@/lib/pc-control";

const activeUpdateStatuses = new Set(["awaitingInstaller", "installing", "awaitingHeartbeat", "rollingBack"]);
const terminalLeaseStates = new Set(["ending", "failed", "completed"]);

const groupToneClassName = {
  primary: "text-primary",
  available: "text-status-available",
  busy: "text-status-busy",
} as const;

/**
 * One action cluster inside the PC card. The group label and tone keep the
 * colour-coded buttons scannable without relying on colour alone: the label
 * says what the cluster is for, the button colour says how risky it is.
 */
function ActionGroup({
  label,
  icon: Icon,
  tone,
  children,
}: {
  label: string;
  icon: LucideIcon;
  tone: keyof typeof groupToneClassName;
  children: ReactNode;
}) {
  return (
    <div role="group" aria-label={label} className="flex flex-col gap-1.5">
      <p
        className={cn(
          "flex items-center gap-1.5 text-xs font-medium",
          groupToneClassName[tone],
        )}
      >
        <Icon aria-hidden className="size-3.5" />
        {label}
      </p>
      <div className="flex flex-wrap gap-1.5">{children}</div>
    </div>
  );
}

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
  onRestartSunshine,
  onUnpairSunshine,
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
  onRestartSunshine: () => void;
  onUnpairSunshine: () => void;
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
              <Button variant="default" size="sm" onClick={onOpenVnc} disabled={busy || !machine.localIp}>
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

        <div className="flex flex-col gap-3">
          <ActionGroup label="เครื่อง" icon={Power} tone="primary">
            <Button variant="success" size="sm" onClick={onWake} disabled={busy || online}>
              <Power data-icon="inline-start" />
              เปิดเครื่อง
            </Button>
            <Button variant="destructive" size="sm" onClick={onPowerOff} disabled={busy || !online}>
              <PowerOff data-icon="inline-start" />
              ปิดเครื่อง
            </Button>
          </ActionGroup>

          <ActionGroup label="ซ่อมบำรุง Sunshine" icon={RefreshCw} tone="busy">
            <Button variant="busy" size="sm" onClick={onRestartSunshine} disabled={busy || !online || updateLocked}>
              <RefreshCw data-icon="inline-start" />
              รีสตาร์ท Sunshine
            </Button>
            <Button variant="busy" size="sm" onClick={onUnpairSunshine} disabled={busy || !online || updateLocked}>
              <Unlink data-icon="inline-start" />
              ยกเลิกการจับคู่ Sunshine
            </Button>
          </ActionGroup>

          <ActionGroup label="เชื่อมต่อ" icon={KeyRound} tone="primary">
            <Button variant="default" size="sm" onClick={onPair} disabled={busy || !leaseActionable}>
              <KeyRound data-icon="inline-start" />
              เชื่อมต่อพิน
            </Button>
          </ActionGroup>

          <ActionGroup label="เวลาเช่า" icon={Clock} tone="primary">
            <Button variant="success" size="sm" onClick={onAddTime} disabled={busy || !leaseActionable}>
              <Plus data-icon="inline-start" />
              +1 ชม.
            </Button>
            <Button variant="busy" size="sm" onClick={onReduceTime} disabled={busy || !leaseActionable}>
              <Minus data-icon="inline-start" />
              −1 ชม.
            </Button>
            <Button variant="default" size="sm" onClick={onTime} disabled={busy || !leaseActionable}>
              <Clock data-icon="inline-start" />
              กำหนดเวลา
            </Button>
            <Button variant="destructive" size="sm" onClick={onCancel} disabled={busy || !leaseActionable}>
              <XCircle data-icon="inline-start" />
              ยกเลิกการเช่า
            </Button>
          </ActionGroup>
        </div>
      </CardContent>
    </Card>
  );
}
