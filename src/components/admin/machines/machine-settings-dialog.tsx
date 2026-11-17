"use client";

import { useRef, useState, type FormEvent } from "react";
import { Activity, Bot, Network, Settings2, ShieldAlert } from "lucide-react";
import { AgentUpdateControl } from "@/components/admin/agent-update-control";
import { MachineDiagnostics } from "@/components/admin/machines/machine-diagnostics";
import { Alert, AlertDescription, AlertTitle } from "@/components/ui/alert";
import {
  AlertDialog,
  AlertDialogAction,
  AlertDialogCancel,
  AlertDialogContent,
  AlertDialogDescription,
  AlertDialogFooter,
  AlertDialogHeader,
  AlertDialogTitle,
} from "@/components/ui/alert-dialog";
import { Badge } from "@/components/ui/badge";
import { Button } from "@/components/ui/button";
import {
  Dialog,
  DialogContent,
  DialogDescription,
  DialogFooter,
  DialogHeader,
  DialogTitle,
} from "@/components/ui/dialog";
import { Field, FieldDescription, FieldError, FieldGroup, FieldLabel } from "@/components/ui/field";
import { Input } from "@/components/ui/input";
import { Spinner } from "@/components/ui/spinner";
import { Tabs, TabsContent, TabsList, TabsTrigger } from "@/components/ui/tabs";
import {
  getSavedVncTargetForMetadataUpdate,
  getVncTargetForExplicitSave,
} from "@/lib/admin-vnc-target-drafts.mjs";
import {
  approvalLabel,
  approvalTone,
  isPendingMachine,
  readinessLabel,
  readinessTone,
} from "@/lib/machine-display";
import type { Machine } from "@/lib/machine-contracts";
import { formatLocalDateTime, formatRamBytes, formatSpec } from "@/lib/machine-specs";

export type MachineMetadataInput = {
  displayName: string;
  hourlyPriceMinorUnits: number;
  moonlightPort: number;
  sunshinePort: number;
  vncLocalIp: string | null;
};

type NetworkCheck =
  | { state: "checking"; targetAddress: string }
  | { state: "supported" | "unsupported"; targetAddress: string; checkedAt: number }
  | { state: "error"; targetAddress: string };

function errorMessage(cause: unknown, fallback: string) {
  return cause instanceof Error && cause.message ? cause.message : fallback;
}

function parsePort(value: string) {
  const parsed = Number(value);
  return Number.isSafeInteger(parsed) && parsed >= 1 && parsed <= 65535 ? parsed : null;
}

const portErrorText = "พอร์ตต้องเป็นจำนวนเต็มระหว่าง 1–65535";

function ReadOnlyRows({ rows }: { rows: Array<{ label: string; value: string; mono?: boolean }> }) {
  return (
    <dl className="grid gap-3 rounded-2xl bg-muted/50 p-4 text-sm sm:grid-cols-2">
      {rows.map((row) => (
        <div key={row.label} className="flex min-w-0 flex-col gap-0.5">
          <dt className="text-xs text-muted-foreground">{row.label}</dt>
          <dd className={row.mono ? "break-all font-mono text-xs font-medium" : "break-words font-medium"}>
            {row.value}
          </dd>
        </div>
      ))}
    </dl>
  );
}

function GeneralTab({
  machine,
  onSave,
  onReject,
}: {
  machine: Machine;
  onSave: (machine: Machine, input: MachineMetadataInput) => Promise<void>;
  onReject: (machine: Machine) => Promise<void>;
}) {
  const [displayName, setDisplayName] = useState(machine.displayName || machine.machineId);
  const [price, setPrice] = useState(String(machine.hourlyPriceMinorUnits));
  const [busy, setBusy] = useState(false);
  const [submitted, setSubmitted] = useState(false);
  const [error, setError] = useState("");
  const [rejectOpen, setRejectOpen] = useState(false);
  const [rejecting, setRejecting] = useState(false);
  const [rejectError, setRejectError] = useState("");

  const trimmedName = displayName.trim();
  const nameError = !trimmedName
    ? "กรอกชื่อที่แสดง"
    : trimmedName.length > 128
      ? "ชื่อต้องไม่เกิน 128 ตัวอักษร"
      : "";
  const priceValue = Number(price);
  const priceInvalid = !Number.isSafeInteger(priceValue) || priceValue < 1;
  const priceError = priceInvalid ? "ราคาต้องเป็นจำนวนเต็มตั้งแต่ 1 สตางค์" : "";
  const pending = isPendingMachine(machine);

  async function submit(event: FormEvent<HTMLFormElement>) {
    event.preventDefault();
    setSubmitted(true);
    setError("");
    if (nameError || priceInvalid || busy) return;
    setBusy(true);
    try {
      await onSave(machine, {
        displayName: trimmedName,
        hourlyPriceMinorUnits: priceValue,
        moonlightPort: machine.moonlightPort,
        sunshinePort: machine.sunshinePort,
        vncLocalIp: getSavedVncTargetForMetadataUpdate(machine),
      });
    } catch (cause) {
      setError(errorMessage(cause, "บันทึกข้อมูลทั่วไปไม่สำเร็จ"));
    } finally {
      setBusy(false);
    }
  }

  async function rejectMachine() {
    if (rejecting) return;
    setRejecting(true);
    setRejectError("");
    try {
      await onReject(machine);
      setRejectOpen(false);
    } catch (cause) {
      setRejectError(errorMessage(cause, "ปฏิเสธเครื่องไม่สำเร็จ"));
    } finally {
      setRejecting(false);
    }
  }

  return (
    <div className="flex flex-col gap-6">
      <form className="flex flex-col gap-6" onSubmit={submit} noValidate>
        <FieldGroup className="gap-4">
          <div className="grid gap-4 sm:grid-cols-2">
            <Field data-invalid={submitted && Boolean(nameError)}>
              <FieldLabel htmlFor="machine-display-name">ชื่อที่แสดง</FieldLabel>
              <Input
                id="machine-display-name"
                value={displayName}
                maxLength={128}
                aria-invalid={submitted && Boolean(nameError)}
                disabled={busy}
                onChange={(event) => setDisplayName(event.currentTarget.value)}
              />
              {submitted && nameError ? (
                <FieldError>{nameError}</FieldError>
              ) : (
                <FieldDescription>ชื่อนี้แสดงให้ผู้เช่าเห็นแทน machine ID</FieldDescription>
              )}
            </Field>
            <Field data-invalid={submitted && priceInvalid}>
              <FieldLabel htmlFor="machine-hourly-price">ราคา (สตางค์/ชั่วโมง)</FieldLabel>
              <Input
                id="machine-hourly-price"
                type="number"
                min={1}
                step={1}
                inputMode="numeric"
                value={price}
                aria-invalid={submitted && priceInvalid}
                disabled={busy}
                onChange={(event) => setPrice(event.currentTarget.value)}
              />
              {submitted && priceError ? (
                <FieldError>{priceError}</FieldError>
              ) : (
                <FieldDescription>ราคาต้องเป็นจำนวนเต็มหน่วยสตางค์</FieldDescription>
              )}
            </Field>
          </div>
        </FieldGroup>
        <ReadOnlyRows
          rows={[
            { label: "Machine ID", value: machine.machineId, mono: true },
            { label: "Hostname", value: formatSpec(machine.hostname) },
            { label: "CPU", value: formatSpec(machine.cpu) },
            { label: "GPU", value: formatSpec(machine.gpu) },
            { label: "RAM", value: formatRamBytes(machine.ramBytes) },
            { label: "ติดต่อล่าสุด", value: formatLocalDateTime(machine.lastSeenAt) },
          ]}
        />
        {error ? (
          <Alert variant="destructive">
            <AlertTitle>บันทึกข้อมูลไม่สำเร็จ</AlertTitle>
            <AlertDescription>{error}</AlertDescription>
          </Alert>
        ) : null}
        <div className="flex justify-end">
          <Button type="submit" disabled={busy}>
            {busy ? <Spinner data-icon="inline-start" /> : null}
            บันทึกข้อมูลทั่วไป
          </Button>
        </div>
      </form>

      {pending ? (
        <div className="flex flex-col gap-3 rounded-2xl border border-status-busy/30 bg-status-busy/10 p-4">
          <div className="flex items-start gap-3">
            <span className="flex size-9 shrink-0 items-center justify-center rounded-xl bg-status-busy/15 text-status-busy">
              <ShieldAlert aria-hidden className="size-4" />
            </span>
            <div className="flex flex-col gap-1">
              <p className="font-medium">เครื่องนี้ยังรออนุมัติ</p>
              <p className="text-sm text-muted-foreground">
                ตั้งค่าชื่อ ราคา และเครือข่ายให้ครบก่อน แล้วอนุมัติจากรายการเครื่องที่รออนุมัติด้านบน
              </p>
            </div>
          </div>
          <div className="flex justify-end">
            <Button type="button" variant="destructive" size="sm" onClick={() => setRejectOpen(true)}>
              ปฏิเสธเครื่องนี้
            </Button>
          </div>
        </div>
      ) : null}

      <AlertDialog open={rejectOpen} onOpenChange={(next) => { if (!rejecting) setRejectOpen(next); }}>
        <AlertDialogContent size="sm">
          <AlertDialogHeader>
            <AlertDialogTitle>ยืนยันปฏิเสธเครื่อง</AlertDialogTitle>
            <AlertDialogDescription>
              ลบเครื่อง {machine.displayName || machine.machineId} ออกจากระบบพร้อมข้อมูล heartbeat
              และ fingerprint — เครื่องต้องลงทะเบียนใหม่จึงจะกลับมาได้
            </AlertDialogDescription>
          </AlertDialogHeader>
          {rejectError ? (
            <Alert variant="destructive">
              <AlertTitle>ปฏิเสธเครื่องไม่สำเร็จ</AlertTitle>
              <AlertDescription>{rejectError}</AlertDescription>
            </Alert>
          ) : null}
          <AlertDialogFooter>
            <AlertDialogCancel disabled={rejecting}>ไม่ใช่ตอนนี้</AlertDialogCancel>
            <AlertDialogAction variant="destructive" disabled={rejecting} onClick={() => void rejectMachine()}>
              {rejecting ? <Spinner data-icon="inline-start" /> : null}
              ปฏิเสธเครื่อง
            </AlertDialogAction>
          </AlertDialogFooter>
        </AlertDialogContent>
      </AlertDialog>
    </div>
  );
}

function NetworkTab({
  machine,
  onSave,
}: {
  machine: Machine;
  onSave: (machine: Machine, input: MachineMetadataInput) => Promise<void>;
}) {
  const [moonlightPort, setMoonlightPort] = useState(String(machine.moonlightPort));
  const [sunshinePort, setSunshinePort] = useState(String(machine.sunshinePort));
  const [vncDraft, setVncDraft] = useState(machine.vncLocalIp ?? "");
  const [check, setCheck] = useState<NetworkCheck | null>(null);
  const [message, setMessage] = useState("");
  const [busy, setBusy] = useState(false);
  const [submitted, setSubmitted] = useState(false);
  const [error, setError] = useState("");
  const checkRun = useRef(0);

  const moonlightInvalid = submitted && parsePort(moonlightPort) === null;
  const sunshineInvalid = submitted && parsePort(sunshinePort) === null;

  function updateVncDraft(value: string) {
    checkRun.current += 1;
    setVncDraft(value);
    setCheck(null);
    setMessage("");
  }

  async function checkVncTarget() {
    const targetAddress = getVncTargetForExplicitSave(vncDraft);
    if (!targetAddress) {
      setCheck(null);
      setMessage("กรอก IPv4 LAN ส่วนตัวหรือ IPv6 ULA ที่ยืนยันกับเครื่องจริงก่อนตรวจสอบ");
      return;
    }
    const requestId = checkRun.current + 1;
    checkRun.current = requestId;
    setMessage("");
    setCheck({ state: "checking", targetAddress });
    try {
      const response = await fetch("/api/admin/vnc-target-check", {
        method: "POST",
        cache: "no-store",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify({ machineId: machine.machineId, targetAddress }),
      });
      if (!response.ok) throw new Error();
      const payload = (await response.json()) as { supported?: boolean };
      if (checkRun.current !== requestId) return;
      setCheck({
        state: payload.supported ? "supported" : "unsupported",
        targetAddress,
        checkedAt: Date.now(),
      });
    } catch {
      if (checkRun.current !== requestId) return;
      setCheck({ state: "error", targetAddress });
    }
  }

  async function submit(event: FormEvent<HTMLFormElement>) {
    event.preventDefault();
    setSubmitted(true);
    setError("");
    const moonlight = parsePort(moonlightPort);
    const sunshine = parsePort(sunshinePort);
    const target = getVncTargetForExplicitSave(vncDraft);
    if (moonlight === null || sunshine === null) return;
    if (!target) {
      setMessage("กรอก IPv4 LAN ส่วนตัวหรือ IPv6 ULA ที่ยืนยันกับเครื่องจริงก่อนบันทึก");
      return;
    }
    if (busy) return;
    setBusy(true);
    try {
      await onSave(machine, {
        displayName: machine.displayName,
        hourlyPriceMinorUnits: machine.hourlyPriceMinorUnits,
        moonlightPort: moonlight,
        sunshinePort: sunshine,
        vncLocalIp: target,
      });
      setMessage("บันทึกการตั้งค่าเครือข่ายแล้ว");
    } catch (cause) {
      setError(errorMessage(cause, "บันทึกการตั้งค่าเครือข่ายไม่สำเร็จ"));
    } finally {
      setBusy(false);
    }
  }

  return (
    <form className="flex flex-col gap-6" onSubmit={submit} noValidate>
      <FieldGroup className="gap-4">
        <div className="grid gap-4 sm:grid-cols-2">
          <Field data-invalid={moonlightInvalid}>
            <FieldLabel htmlFor="machine-moonlight-port">Moonlight port</FieldLabel>
            <Input
              id="machine-moonlight-port"
              type="number"
              min={1}
              max={65535}
              step={1}
              inputMode="numeric"
              value={moonlightPort}
              aria-invalid={moonlightInvalid}
              disabled={busy}
              onChange={(event) => setMoonlightPort(event.currentTarget.value)}
            />
            {moonlightInvalid ? <FieldError>{portErrorText}</FieldError> : <FieldDescription>พอร์ต GameStream ที่ Moonlight ใช้</FieldDescription>}
          </Field>
          <Field data-invalid={sunshineInvalid}>
            <FieldLabel htmlFor="machine-sunshine-port">Management port</FieldLabel>
            <Input
              id="machine-sunshine-port"
              type="number"
              min={1}
              max={65535}
              step={1}
              inputMode="numeric"
              value={sunshinePort}
              aria-invalid={sunshineInvalid}
              disabled={busy}
              onChange={(event) => setSunshinePort(event.currentTarget.value)}
            />
            {sunshineInvalid ? <FieldError>{portErrorText}</FieldError> : <FieldDescription>พอร์ต HTTPS ของ Sunshine management</FieldDescription>}
          </Field>
        </div>
        <Field>
          <FieldLabel htmlFor="machine-vnc-target">IP เป้าหมาย VNC (ตรวจสอบกับเครื่องจริงก่อนบันทึก)</FieldLabel>
          <div className="flex flex-col gap-2 sm:flex-row">
            <Input
              id="machine-vnc-target"
              value={vncDraft}
              disabled={busy}
              onChange={(event) => updateVncDraft(event.currentTarget.value)}
            />
            <Button
              type="button"
              variant="outline"
              className="shrink-0"
              disabled={busy || check?.state === "checking"}
              onClick={() => void checkVncTarget()}
            >
              {check?.state === "checking" ? "กำลังตรวจสอบ VNC…" : "ตรวจสอบ VNC"}
            </Button>
          </div>
          <FieldDescription>
            รับเฉพาะ IPv4 LAN ส่วนตัว (10/8, 172.16/12, 192.168/16) หรือ IPv6 ULA เท่านั้น
          </FieldDescription>
        </Field>
        {check ? (
          <p role="status" aria-live="polite" className="text-sm text-muted-foreground">
            {check.state === "checking"
              ? `กำลังตรวจ ${check.targetAddress}:5900…`
              : check.state === "supported"
                ? `พบ VNC ที่รองรับบน ${check.targetAddress}:5900 · ตรวจเมื่อ ${new Date(check.checkedAt).toLocaleString()} ตามเวลาท้องถิ่น (ยังไม่ได้ยืนยันรหัสผ่าน)`
                : check.state === "unsupported"
                  ? `ไม่พบ VNC ที่รองรับบน ${check.targetAddress}:5900`
                  : `ตรวจสอบ ${check.targetAddress}:5900 ไม่สำเร็จ`}
          </p>
        ) : null}
        {message ? <p role="status" aria-live="polite" className="text-sm">{message}</p> : null}
      </FieldGroup>
      {error ? (
        <Alert variant="destructive">
          <AlertTitle>บันทึกการตั้งค่าเครือข่ายไม่สำเร็จ</AlertTitle>
          <AlertDescription>{error}</AlertDescription>
        </Alert>
      ) : null}
      <div className="flex justify-end">
        <Button type="submit" disabled={busy || check?.state === "checking"}>
          {busy ? <Spinner data-icon="inline-start" /> : null}
          บันทึกการตั้งค่าเครือข่าย
        </Button>
      </div>
    </form>
  );
}

function AgentTab({
  machine,
  actorId,
  onUpdated,
}: {
  machine: Machine;
  actorId: string;
  onUpdated: () => Promise<boolean>;
}) {
  const ack =
    machine.appliedConfigRevision === null
      ? "ยังไม่ยืนยัน"
      : machine.appliedConfigRevision === machine.configRevision
        ? `${machine.appliedConfigRevision} (ตรงกับ config)`
        : `${machine.appliedConfigRevision} (รอ Agent ACK)`;
  return (
    <div className="flex flex-col gap-6">
      <div className="flex flex-col gap-3">
        <p className="text-sm">
          เวอร์ชัน Agent: {machine.agentVersion || "ยังไม่รายงาน"}
        </p>
        <ReadOnlyRows
          rows={[
            { label: "IP ที่ Agent รายงาน", value: machine.localIp || "—", mono: true },
            { label: "Fingerprint", value: machine.publicKeyFingerprint || "ยังไม่มีข้อมูลยืนยัน", mono: true },
            { label: "Config revision", value: `${machine.configRevision} · ACK ${ack}` },
            {
              label: "Sunshine credential revision ที่ Agent ใช้",
              value: String(machine.appliedSunshineCredentialRevision),
            },
            { label: "ติดต่อล่าสุด", value: formatLocalDateTime(machine.lastSeenAt) },
          ]}
        />
      </div>
      <AgentUpdateControl
        key={`${actorId}:${machine.machineId}`}
        machine={machine}
        actorId={actorId}
        onUpdated={onUpdated}
      />
    </div>
  );
}

export function MachineSettingsDialog({
  machine,
  open,
  actorId,
  onOpenChange,
  onSave,
  onReject,
  onUpdated,
}: {
  machine: Machine | null;
  open: boolean;
  actorId: string;
  onOpenChange: (open: boolean) => void;
  onSave: (machine: Machine, input: MachineMetadataInput) => Promise<void>;
  onReject: (machine: Machine) => Promise<void>;
  onUpdated: () => Promise<boolean>;
}) {
  return (
    <Dialog open={open} onOpenChange={onOpenChange}>
      <DialogContent className="max-h-[85dvh] overflow-y-auto sm:max-w-3xl">
        <DialogHeader>
          <DialogTitle>
            {machine ? `ตั้งค่าเครื่อง ${machine.displayName || machine.machineId}` : "ตั้งค่าเครื่อง"}
          </DialogTitle>
          <DialogDescription>
            {machine ? (
              <span className="flex flex-wrap items-center gap-2">
                <span className="font-mono text-xs">{machine.machineId}</span>
                <Badge variant={approvalTone(machine.approvalStatus)}>{approvalLabel(machine.approvalStatus)}</Badge>
                <Badge variant={readinessTone(machine.readiness)}>{readinessLabel(machine.readiness)}</Badge>
              </span>
            ) : (
              "ไม่พบข้อมูลเครื่องล่าสุด"
            )}
          </DialogDescription>
        </DialogHeader>
        {machine ? (
          <Tabs defaultValue="general" className="gap-6">
            <TabsList className="w-full justify-start overflow-x-auto sm:w-fit">
              <TabsTrigger value="general">
                <Settings2 aria-hidden />
                ทั่วไป
              </TabsTrigger>
              <TabsTrigger value="network">
                <Network aria-hidden />
                เครือข่าย
              </TabsTrigger>
              <TabsTrigger value="agent">
                <Bot aria-hidden />
                Agent
              </TabsTrigger>
              <TabsTrigger value="diagnostics">
                <Activity aria-hidden />
                ผลตรวจ
              </TabsTrigger>
            </TabsList>
            <TabsContent value="general" keepMounted className="min-h-80">
              <GeneralTab machine={machine} onSave={onSave} onReject={onReject} />
            </TabsContent>
            <TabsContent value="network" keepMounted className="min-h-80">
              <NetworkTab machine={machine} onSave={onSave} />
            </TabsContent>
            <TabsContent value="agent" keepMounted className="min-h-80">
              <AgentTab machine={machine} actorId={actorId} onUpdated={onUpdated} />
            </TabsContent>
            <TabsContent value="diagnostics" keepMounted className="min-h-80">
              <MachineDiagnostics machine={machine} />
            </TabsContent>
          </Tabs>
        ) : null}
        <DialogFooter>
          <Button type="button" variant="outline" onClick={() => onOpenChange(false)}>
            ปิด
          </Button>
        </DialogFooter>
      </DialogContent>
    </Dialog>
  );
}
