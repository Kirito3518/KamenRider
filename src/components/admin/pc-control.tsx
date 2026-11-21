"use client";

import { useCallback, useEffect, useRef, useState } from "react";
import { Monitor, RefreshCw } from "lucide-react";
import { AdminPage, AdminPageHeader } from "@/components/admin/admin-page";
import { Alert, AlertDescription, AlertTitle } from "@/components/ui/alert";
import { Badge } from "@/components/ui/badge";
import { Button } from "@/components/ui/button";
import { Empty, EmptyDescription, EmptyHeader, EmptyMedia, EmptyTitle } from "@/components/ui/empty";
import { Skeleton } from "@/components/ui/skeleton";
import { Spinner } from "@/components/ui/spinner";
import { cn } from "@/lib/utils";
import { PcControlCard } from "@/components/admin/pc-control-card";
import {
  PcControlConfirmDialog,
  PcControlPairDialog,
  PcControlTimeDialog,
} from "@/components/admin/pc-control-dialogs";
import {
  sunshineCommandLabel,
  sunshineCommandStatusLabels,
  type PcControlMachine,
  type PcControlSnapshot,
  type SunshineCommandAction,
  type SunshineCommandStatus,
} from "@/lib/pc-control";

type ConfirmTarget = { kind: "powerOff" | "cancel" | "restartSunshine" | "unpairSunshine"; machine: PcControlMachine } | null;

const buttonToneLegend = [
  { label: "เปิดใช้งาน / เพิ่มเวลา", dotClassName: "bg-status-available" },
  { label: "เชื่อมต่อ / ตั้งค่า", dotClassName: "bg-primary" },
  { label: "ซ่อมบำรุง / ลดเวลา", dotClassName: "bg-status-busy" },
  { label: "ปิด / ยกเลิก", dotClassName: "bg-destructive" },
  { label: "ทั่วไป / รีเฟรช", dotClassName: "bg-muted-foreground" },
] as const;

async function postJson(path: string, body: unknown) {
  const response = await fetch(path, {
    method: "POST",
    cache: "no-store",
    headers: { "Content-Type": "application/json" },
    body: JSON.stringify(body),
  });
  const payload = (await response.json().catch(() => ({}))) as { error?: string };
  if (!response.ok) throw new Error(payload.error || `คำขอไม่สำเร็จ (${response.status})`);
  return payload;
}

export function PcControl() {
  const [snapshot, setSnapshot] = useState<PcControlSnapshot | null>(null);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState("");
  const [message, setMessage] = useState("");
  const [busyMachine, setBusyMachine] = useState<string | null>(null);
  const [openVnc, setOpenVnc] = useState<string | null>(null);
  const [pairTarget, setPairTarget] = useState<PcControlMachine | null>(null);
  const [timeTarget, setTimeTarget] = useState<PcControlMachine | null>(null);
  const [confirmTarget, setConfirmTarget] = useState<ConfirmTarget>(null);
  const [now, setNow] = useState(() => Date.now());
  const mounted = useRef(true);

  const load = useCallback(async (quiet = false) => {
    if (!quiet) setLoading(true);
    try {
      const response = await fetch("/api/admin/pc-control", { cache: "no-store" });
      const payload = (await response.json()) as PcControlSnapshot | { error?: string };
      if (!response.ok) throw new Error("error" in payload ? payload.error : undefined);
      const next = payload as PcControlSnapshot;
      if (mounted.current) {
        setSnapshot(next);
        setError("");
      }
    } catch (cause) {
      if (mounted.current) setError(cause instanceof Error ? cause.message : "โหลดข้อมูลเครื่องไม่สำเร็จ");
    } finally {
      if (mounted.current) setLoading(false);
    }
  }, []);

  useEffect(() => {
    mounted.current = true;
    const initial = window.setTimeout(() => void load(), 0);
    const poll = window.setInterval(() => void load(true), 5_000);
    const clock = window.setInterval(() => setNow(Date.now()), 1_000);
    return () => {
      mounted.current = false;
      window.clearTimeout(initial);
      window.clearInterval(poll);
      window.clearInterval(clock);
    };
  }, [load]);

  async function run(machine: PcControlMachine, action: () => Promise<unknown>, success: string) {
    if (busyMachine) return;
    setBusyMachine(machine.machineId);
    setMessage("");
    setError("");
    try {
      await action();
      setMessage(success);
      await load(true);
    } catch (cause) {
      setError(cause instanceof Error ? cause.message : "ดำเนินการไม่สำเร็จ");
    } finally {
      setBusyMachine(null);
    }
  }

  async function wake(machine: PcControlMachine) {
    await run(machine, () => postJson(`/api/admin/machines/${encodeURIComponent(machine.machineId)}/wake`, {}), `ส่งคำสั่งเปิดเครื่อง ${machine.displayName || machine.machineId} แล้ว`);
  }

  async function adjustTime(machine: PcControlMachine, deltaSeconds: number) {
    const lease = machine.lease;
    if (!lease) return;
    await run(machine, () => postJson(`/api/admin/pc-control/${encodeURIComponent(machine.machineId)}/lease-time`, {
      deltaSeconds,
      expectedRevision: lease.revision,
      idempotencyKey: crypto.randomUUID(),
    }), deltaSeconds > 0 ? "ส่งคำขอเพิ่มเวลาแล้ว รอ Agent ยืนยัน" : "ส่งคำขอลดเวลาแล้ว รอ Agent ยืนยัน");
  }

  async function queueSunshine(machine: PcControlMachine, action: SunshineCommandAction) {
    const label = sunshineCommandLabel(action);
    const force = machine.lease !== null;
    await run(machine, async () => {
      const created = (await postJson(
        `/api/admin/pc-control/${encodeURIComponent(machine.machineId)}/sunshine`,
        { action, force, requestId: crypto.randomUUID() },
      )) as SunshineCommandStatus;
      if (typeof created.commandId === "string" && created.commandId)
        void trackSunshine(machine, action, created.commandId);
      return created;
    }, `ส่งคำสั่ง${label}แล้ว รอ Agent ยืนยัน`);
  }

  // The Controller keeps the command status until the Agent reports back; poll briefly so the
  // admin sees applied/failed instead of only the queued acknowledgement.
  async function trackSunshine(machine: PcControlMachine, action: SunshineCommandAction, commandId: string) {
    const label = sunshineCommandLabel(action);
    const name = machine.displayName || machine.machineId;
    for (let attempt = 0; attempt < 30; attempt += 1) {
      if (attempt > 0) await new Promise((resolve) => window.setTimeout(resolve, 2_000));
      if (!mounted.current) return;
      try {
        const response = await fetch(
          `/api/admin/pc-control/${encodeURIComponent(machine.machineId)}/sunshine/${encodeURIComponent(commandId)}`,
          { cache: "no-store" },
        );
        if (!response.ok) return;
        const status = (await response.json()) as SunshineCommandStatus;
        if (!mounted.current) return;
        if (status.status === "applied") {
          setMessage(`${label} ${name} สำเร็จแล้ว`);
          return;
        }
        if (status.status === "failed" || status.status === "expired") {
          setError(`${label} ${name} ไม่สำเร็จ (${sunshineCommandStatusLabels[status.status] || status.status})`);
          return;
        }
      } catch {
        return;
      }
    }
  }

  async function confirmAction() {
    if (!confirmTarget) return;
    const { kind, machine } = confirmTarget;
    if (kind === "powerOff") {
      await run(machine, () => postJson(`/api/admin/pc-control/${encodeURIComponent(machine.machineId)}/power-off`, {
        requestId: crypto.randomUUID(),
      }), "ส่งคำสั่งปิดเครื่องและสิ้นสุดการเช่าแล้ว");
    } else if (kind === "cancel") {
      const lease = machine.lease;
      if (!lease) return;
      await run(machine, () => postJson(`/api/admin/pc-control/${encodeURIComponent(machine.machineId)}/lease-cancel`, {
        expectedRevision: lease.revision,
        idempotencyKey: crypto.randomUUID(),
        requestId: crypto.randomUUID(),
      }), "ส่งคำขอยกเลิกการเช่าแล้ว รอ Agent ยืนยัน");
    } else {
      await queueSunshine(machine, kind === "restartSunshine" ? "restart" : "unpair");
    }
    setConfirmTarget(null);
  }

  const machines = snapshot?.machines ?? [];

  return (
    <AdminPage>
      <AdminPageHeader
        icon={Monitor}
        title="PC Control"
        titleSuffix={<Badge variant="outline">{machines.length} เครื่อง</Badge>}
        description="ควบคุมเครื่องที่ให้เช่า เปิดจอ VNC เมื่อต้องการ และจัดการเวลา/การเช่าได้จากการ์ดเดียว"
        actions={
          <Button variant="outline" size="sm" onClick={() => void load()} disabled={loading}>
            {loading ? <Spinner data-icon="inline-start" /> : <RefreshCw data-icon="inline-start" />}
            รีเฟรช
          </Button>
        }
      />

      <div
        role="note"
        aria-label="คำอธิบายสีของปุ่ม"
        className="flex flex-wrap items-center gap-x-4 gap-y-2 rounded-2xl border bg-card px-4 py-3 text-xs text-muted-foreground"
      >
        <span className="font-medium text-foreground">สีของปุ่ม</span>
        {buttonToneLegend.map(({ label, dotClassName }) => (
          <span key={label} className="flex items-center gap-1.5">
            <span
              aria-hidden
              className={cn("size-2 rounded-full", dotClassName)}
            />
            {label}
          </span>
        ))}
      </div>

      {error ? (
        <Alert variant="destructive">
          <AlertTitle>ดำเนินการไม่สำเร็จ</AlertTitle>
          <AlertDescription>{error}</AlertDescription>
        </Alert>
      ) : null}
      {message ? (
        <Alert>
          <AlertTitle>สถานะล่าสุด</AlertTitle>
          <AlertDescription>{message}</AlertDescription>
        </Alert>
      ) : null}

      {loading && !snapshot ? (
        <div className="grid gap-4 md:grid-cols-2 xl:grid-cols-3">
          {Array.from({ length: 3 }, (_, index) => (
            <Skeleton key={index} className="h-80 w-full rounded-2xl" />
          ))}
        </div>
      ) : machines.length === 0 ? (
        <Empty>
          <EmptyHeader>
            <EmptyMedia variant="icon">
              <Monitor aria-hidden />
            </EmptyMedia>
            <EmptyTitle>ยังไม่มีเครื่องในระบบ</EmptyTitle>
            <EmptyDescription>เพิ่มและอนุมัติเครื่องจากหน้า “เครื่องและ Agent” ก่อน</EmptyDescription>
          </EmptyHeader>
        </Empty>
      ) : (
        <div className="grid gap-4 md:grid-cols-2 xl:grid-cols-3">
          {machines.map((machine) => (
            <PcControlCard
              key={machine.machineId}
              machine={machine}
              now={now}
              busy={busyMachine === machine.machineId}
              vncOpen={openVnc === machine.machineId}
              onWake={() => void wake(machine)}
              onOpenVnc={() => setOpenVnc(machine.machineId)}
              onCloseVnc={() => setOpenVnc((current) => (current === machine.machineId ? null : current))}
              onPair={() => setPairTarget(machine)}
              onTime={() => setTimeTarget(machine)}
              onAddTime={() => void adjustTime(machine, 3600)}
              onReduceTime={() => void adjustTime(machine, -3600)}
              onPowerOff={() => setConfirmTarget({ kind: "powerOff", machine })}
              onCancel={() => setConfirmTarget({ kind: "cancel", machine })}
              onRestartSunshine={() => setConfirmTarget({ kind: "restartSunshine", machine })}
              onUnpairSunshine={() => setConfirmTarget({ kind: "unpairSunshine", machine })}
            />
          ))}
        </div>
      )}

      <PcControlConfirmDialog
        open={confirmTarget !== null}
        busy={confirmTarget !== null && busyMachine === confirmTarget.machine.machineId}
        kind={confirmTarget?.kind ?? "powerOff"}
        machine={confirmTarget?.machine ?? null}
        onClose={() => setConfirmTarget(null)}
        onConfirm={() => void confirmAction()}
      />
      <PcControlPairDialog
        key={"pair:" + (pairTarget?.machineId ?? "closed")}
        machine={pairTarget}
        onClose={() => setPairTarget(null)}
        onCompleted={() => void load(true)}
      />
      <PcControlTimeDialog
        key={"time:" + (timeTarget?.machineId ?? "closed")}
        machine={timeTarget}
        busy={timeTarget !== null && busyMachine === timeTarget.machineId}
        onClose={() => setTimeTarget(null)}
        onSubmit={(delta) => {
          if (!timeTarget) return;
          void adjustTime(timeTarget, delta);
          setTimeTarget(null);
        }}
      />
    </AdminPage>
  );
}
