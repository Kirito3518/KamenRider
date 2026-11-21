"use client";

import { useCallback, useEffect, useRef, useState } from "react";
import toast from "react-hot-toast";
import { AlertTriangle, Download, Monitor, RefreshCw } from "lucide-react";
import { AdminPage, AdminPageHeader } from "@/components/admin/admin-page";
import { AgentInstallDialog } from "@/components/admin/machines/agent-install-dialog";
import { ApprovedMachinesTable } from "@/components/admin/machines/approved-machines-table";
import {
  MachineSettingsDialog,
  type MachineMetadataInput,
  type MachineSettingsMode,
} from "@/components/admin/machines/machine-settings-dialog";
import { MachineStats } from "@/components/admin/machines/machine-stats";
import { PendingMachines } from "@/components/admin/machines/pending-machines";
import { Alert, AlertDescription, AlertTitle } from "@/components/ui/alert";
import { Badge } from "@/components/ui/badge";
import { Button } from "@/components/ui/button";
import { Skeleton } from "@/components/ui/skeleton";
import { Spinner } from "@/components/ui/spinner";
import { CardsGridSkeleton, LoadingRegion, StatGridSkeleton, TableSkeleton } from "@/components/loading";
import type { Machine } from "@/lib/machine-contracts";

type SettingsTarget = { machineId: string; mode: MachineSettingsMode };

async function responseError(response: Response, fallback: string) {
  const payload = (await response.json().catch(() => ({}))) as { error?: unknown };
  const detail = typeof payload.error === "string" && payload.error ? payload.error : `HTTP ${response.status}`;
  return new Error(`${fallback} (${detail})`);
}

export function MachinesManager({ actorId }: { actorId: string }) {
  const [machines, setMachines] = useState<Machine[] | null>(null);
  const [loadError, setLoadError] = useState("");
  const [now, setNow] = useState(0);
  const [loading, setLoading] = useState(false);
  const [message, setMessage] = useState("");
  const [installOpen, setInstallOpen] = useState(false);
  const [settingsTarget, setSettingsTarget] = useState<SettingsTarget | null>(null);
  const latestLoad = useRef(0);

  const load = useCallback(async (signal?: AbortSignal): Promise<boolean> => {
    const run = ++latestLoad.current;
    try {
      const response = await fetch("/api/admin/machines", { cache: "no-store", signal });
      if (!response.ok) throw new Error("โหลดข้อมูลเครื่องไม่ได้");
      const nextMachines = (await response.json()) as Machine[];
      if (signal?.aborted || run !== latestLoad.current) return false;
      setMachines(nextMachines);
      setSettingsTarget((current) =>
        current && !nextMachines.some((machine) => machine.machineId === current.machineId) ? null : current,
      );
      setNow(Date.now());
      setLoadError("");
      return true;
    } catch (cause) {
      if (signal?.aborted || run !== latestLoad.current) return false;
      setLoadError(cause instanceof Error && cause.message ? cause.message : "โหลดข้อมูลเครื่องไม่ได้");
      return false;
    }
  }, []);

  useEffect(() => {
    const loads = latestLoad;
    const controller = new AbortController();
    let busy = false;
    async function refresh() {
      if (busy) return;
      busy = true;
      try {
        await load(controller.signal);
      } finally {
        busy = false;
      }
    }
    void refresh();
    const timer = window.setInterval(() => {
      if (document.visibilityState === "visible") void refresh();
    }, 5000);
    return () => {
      loads.current++;
      controller.abort();
      window.clearInterval(timer);
    };
  }, [load]);

  async function refreshManually() {
    setLoading(true);
    try {
      const ok = await load();
      if (!ok) toast.error("โหลดข้อมูลเครื่องไม่สำเร็จ");
    } finally {
      setLoading(false);
    }
  }

  async function saveMachineSettings(machine: Machine, input: MachineMetadataInput) {
    const response = await fetch(`/api/admin/machines/${encodeURIComponent(machine.machineId)}`, {
      method: "PATCH",
      cache: "no-store",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify({ ...input, expectedRevision: machine.configRevision }),
    });
    if (!response.ok) throw await responseError(response, "บันทึกการตั้งค่าไม่สำเร็จ");
    setMessage(`บันทึกการตั้งค่า ${machine.displayName || machine.machineId} แล้ว`);
    toast.success("บันทึกการตั้งค่าแล้ว");
    await load();
  }

  async function approveMachine(machine: Machine) {
    const fingerprint = machine.publicKeyFingerprint;
    if (!fingerprint) throw new Error("Agent ยังไม่รายงาน fingerprint จึงยังอนุมัติไม่ได้");
    const response = await fetch(`/api/admin/machines/${encodeURIComponent(machine.machineId)}/approve`, {
      method: "POST",
      cache: "no-store",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify({ fingerprint }),
    });
    if (!response.ok) throw await responseError(response, "อนุมัติเครื่องไม่สำเร็จ");
    setSettingsTarget(null);
    setMessage(`อนุมัติเครื่อง ${machine.displayName || machine.machineId} แล้ว`);
    toast.success("อนุมัติเครื่องแล้ว");
    await load();
  }

  async function rejectMachine(machine: Machine) {
    if (!machine.publicKeyFingerprint) throw new Error("ไม่พบ fingerprint ของเครื่องนี้");
    const response = await fetch(`/api/admin/machines/${encodeURIComponent(machine.machineId)}/reject`, {
      method: "POST",
      cache: "no-store",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify({ fingerprint: machine.publicKeyFingerprint }),
    });
    if (!response.ok) throw await responseError(response, "ปฏิเสธเครื่องไม่สำเร็จ");
    setSettingsTarget(null);
    setMessage(`ปฏิเสธเครื่อง ${machine.displayName || machine.machineId} แล้ว`);
    toast.success("ปฏิเสธเครื่องแล้ว");
    await load();
  }

  const settingsMachine = machines?.find((machine) => machine.machineId === settingsTarget?.machineId) ?? null;
  const settingsMode: MachineSettingsMode = settingsTarget?.mode ?? "settings";

  return (
    <AdminPage>
      <AdminPageHeader
        icon={Monitor}
        title="เครื่องและ Agent"
        titleSuffix={
          <Badge variant="outline">
            {machines === null ? <Skeleton className="h-3.5 w-12 rounded-full" /> : `${machines.length.toLocaleString("th-TH")} เครื่อง`}
          </Badge>
        }
        description="อนุมัติเครื่องใหม่ ตั้งค่าชื่อ ราคา และเครือข่าย ดูแล Agent update จากที่เดียว — การควบคุมเครื่อง การเช่า และ VNC อยู่ที่ PC Control"
        actions={
          <>
            <Button type="button" variant="outline" size="sm" disabled={loading} onClick={() => void refreshManually()}>
              {loading ? <Spinner data-icon="inline-start" /> : <RefreshCw data-icon="inline-start" />}
              รีเฟรช
            </Button>
            <Button type="button" size="sm" onClick={() => setInstallOpen(true)}>
              <Download data-icon="inline-start" />
              ติดตั้ง Agent
            </Button>
          </>
        }
      />

      {loadError ? (
        <Alert variant="destructive">
          <AlertTriangle aria-hidden />
          <AlertTitle>โหลดข้อมูลเครื่องไม่สำเร็จ</AlertTitle>
          <AlertDescription>
            <p>{loadError}</p>
            <Button
              type="button"
              variant="outline"
              size="sm"
              className="mt-2"
              disabled={loading}
              onClick={() => void refreshManually()}
            >
              ลองใหม่
            </Button>
          </AlertDescription>
        </Alert>
      ) : null}

      {machines === null && !loadError ? (
        <LoadingRegion label="กำลังโหลดข้อมูลเครื่องและ Agent" className="flex flex-col gap-4">
          <StatGridSkeleton count={4} />
          <CardsGridSkeleton count={1} rows={1} columns="grid-cols-1" cardClassName="h-28" />
          <TableSkeleton rows={4} columns={6} />
        </LoadingRegion>
      ) : (
        <>
          <MachineStats machines={machines} now={now} />

          <PendingMachines
            machines={machines}
            onOpenSettings={(machineId) => setSettingsTarget({ machineId, mode: "settings" })}
            onApprove={(machineId) => setSettingsTarget({ machineId, mode: "approve" })}
          />

          <ApprovedMachinesTable
            machines={machines}
            now={now}
            onOpenSettings={(machineId) => setSettingsTarget({ machineId, mode: "settings" })}
            onOpenInstall={() => setInstallOpen(true)}
          />
        </>
      )}

      <p className="text-xs text-muted-foreground">
        ระบบเช่าจริง — ราคาต่อชั่วโมงจะถูกหักจากยอดเงินสมาชิกเมื่อมีการเช่า และความพร้อมจริงยังต้องผ่านการตรวจสอบ Agent
      </p>
      <p role="status" aria-live="polite" className="sr-only">
        {message}
      </p>

      <AgentInstallDialog open={installOpen} onOpenChange={setInstallOpen} />

      <MachineSettingsDialog
        key={`${settingsTarget?.machineId ?? "closed"}:${settingsMode}`}
        machine={settingsMachine}
        mode={settingsMode}
        open={settingsTarget !== null}
        actorId={actorId}
        onOpenChange={(open) => {
          if (!open) setSettingsTarget(null);
        }}
        onSave={saveMachineSettings}
        onReject={rejectMachine}
        onUpdated={load}
        onApprove={approveMachine}
      />
    </AdminPage>
  );
}
