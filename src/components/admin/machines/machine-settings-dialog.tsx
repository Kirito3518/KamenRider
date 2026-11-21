"use client";

import { useRef, useState } from "react";
import { Activity, Bot, Network, Settings2, ShieldAlert, ShieldCheck } from "lucide-react";
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
  AlertDialogMedia,
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
import { Select, SelectContent, SelectItem, SelectTrigger, SelectValue } from "@/components/ui/select";
import { Spinner } from "@/components/ui/spinner";
import { Tabs, TabsContent, TabsList, TabsTrigger } from "@/components/ui/tabs";
import { getVncTargetForExplicitSave } from "@/lib/admin-vnc-target-drafts.mjs";
import {
  approvalLabel,
  approvalTone,
  isPendingMachine,
  readinessLabel,
  readinessTone,
} from "@/lib/machine-display";
import type { Machine } from "@/lib/machine-contracts";
import { formatBalance } from "@/lib/money";
import { formatLocalDateTime, formatRamBytes, formatSpec } from "@/lib/machine-specs";

export type MachineMetadataInput = {
  displayName: string;
  hourlyPriceMinorUnits: number;
  moonlightPort: number;
  sunshinePort: number;
  vncLocalIp: string | null;
  parsecEnabled: boolean;
  availability: string;
};

export type MachineSettingsMode = "settings" | "approve";

type SettingsDraft = {
  displayName: string;
  price: string;
  parsecEnabled: boolean;
  availability: string;
  moonlightPort: string;
  sunshinePort: string;
  vncDraft: string;
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

function draftFromMachine(machine: Machine | null): SettingsDraft {
  return {
    displayName: machine?.displayName || machine?.machineId || "",
    price: machine ? String(machine.hourlyPriceMinorUnits / 100) : "",
    parsecEnabled: machine?.parsecEnabled === true,
    availability: machine?.availability ?? "rentable",
    moonlightPort: machine ? String(machine.moonlightPort) : "",
    sunshinePort: machine ? String(machine.sunshinePort) : "",
    vncDraft: machine?.vncLocalIp ?? "",
  };
}

const portErrorText = "พอร์ตต้องเป็นจำนวนเต็มระหว่าง 1–65535";
const vncErrorText =
  "กรอก IPv4 LAN ส่วนตัว (10/8, 172.16/12, 192.168/16) หรือ IPv6 ULA เท่านั้น";

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
  draft,
  onChange,
  submitted,
  nameError,
  priceError,
  disabled,
  approveMode,
  onReject,
}: {
  machine: Machine;
  draft: SettingsDraft;
  onChange: (patch: Partial<SettingsDraft>) => void;
  submitted: boolean;
  nameError: string;
  priceError: string;
  disabled: boolean;
  approveMode: boolean;
  onReject: (machine: Machine) => Promise<void>;
}) {
  const [rejectOpen, setRejectOpen] = useState(false);
  const [rejecting, setRejecting] = useState(false);
  const [rejectError, setRejectError] = useState("");
  const pending = isPendingMachine(machine);

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
      <FieldGroup className="gap-4">
        <div className="grid gap-4 sm:grid-cols-2">
          <Field data-invalid={submitted && Boolean(nameError)}>
            <FieldLabel htmlFor="machine-display-name">ชื่อที่แสดง</FieldLabel>
            <Input
              id="machine-display-name"
              value={draft.displayName}
              maxLength={128}
              aria-invalid={submitted && Boolean(nameError)}
              disabled={disabled}
              onChange={(event) => onChange({ displayName: event.currentTarget.value })}
            />
            {submitted && nameError ? (
              <FieldError>{nameError}</FieldError>
            ) : (
              <FieldDescription>ชื่อนี้แสดงให้ผู้เช่าเห็นแทน machine ID</FieldDescription>
            )}
          </Field>
          <Field data-invalid={submitted && Boolean(priceError)}>
            <FieldLabel htmlFor="machine-hourly-price">ราคา (บาท/ชั่วโมง)</FieldLabel>
            <Input
              id="machine-hourly-price"
              type="number"
              min={1}
              step={1}
              inputMode="numeric"
              value={draft.price}
              aria-invalid={submitted && Boolean(priceError)}
              disabled={disabled}
              onChange={(event) => onChange({ price: event.currentTarget.value })}
            />
            {submitted && priceError ? (
              <FieldError>{priceError}</FieldError>
            ) : (
              <FieldDescription>ราคาต้องเป็นจำนวนเต็มบาท (เช่น 10, 20, 30)</FieldDescription>
            )}
          </Field>
        </div>
      </FieldGroup>
      <Field>
        <label className="flex items-start gap-3 text-sm">
          <input
            type="checkbox"
            className="mt-0.5 size-4"
            checked={draft.parsecEnabled}
            disabled={disabled}
            onChange={(event) => onChange({ parsecEnabled: event.currentTarget.checked })}
          />
          <span className="flex flex-col gap-1">
            <span className="font-medium">เปิดให้ลูกค้าเข้าสู่ระบบ Parsec ด้วยบัญชีของตัวเอง</span>
            <span className="text-muted-foreground">
              Agent จะแสดงปุ่ม “เชื่อมต่อด้วย Parsec” เฉพาะเมื่อตรวจพบ Parsec บนเครื่องนี้
              {machine.parsecAvailable === true ? " (ตรวจพบแล้ว)" : " (ยังไม่ตรวจพบ)"}
              — ลูกค้ากรอกรหัสในเบราว์เซอร์และถูกเข้ารหัสก่อนส่งถึงเครื่อง
            </span>
          </span>
        </label>
      </Field>
      <Field>
        <FieldLabel htmlFor="machine-availability">สถานะการให้เช่า</FieldLabel>
        <Select
          value={draft.availability}
          onValueChange={(value) => onChange({ availability: String(value ?? "rentable") })}
        >
          <SelectTrigger id="machine-availability" className="w-full">
            <SelectValue placeholder="เลือกสถานะ" />
          </SelectTrigger>
          <SelectContent>
            <SelectItem value="rentable">เปิดให้เช่า</SelectItem>
            <SelectItem value="maintenance">ซ่อมบำรุง</SelectItem>
            <SelectItem value="disabled">ปิดให้เช่า</SelectItem>
          </SelectContent>
        </Select>
        <FieldDescription>
          โหมดนี้มีผลกับการเช่าใหม่ — เครื่องที่กำลังเช่าอยู่ต้องยกเลิกหรือรอหมดเวลาก่อน
        </FieldDescription>
      </Field>
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

      {pending ? (
        <div className="flex flex-col gap-3 rounded-2xl border border-status-busy/30 bg-status-busy/10 p-4">
          <div className="flex items-start gap-3">
            <span className="flex size-9 shrink-0 items-center justify-center rounded-xl bg-status-busy/15 text-status-busy">
              <ShieldAlert aria-hidden className="size-4" />
            </span>
            <div className="flex flex-col gap-1">
              <p className="font-medium">เครื่องนี้ยังรออนุมัติ</p>
              <p className="text-sm text-muted-foreground">
                {approveMode
                  ? "ตั้งค่าชื่อ ราคา และเครือข่ายให้ครบ แล้วกด “ยืนยัน” ด้านล่างเพื่อตรวจสอบและอนุมัติ"
                  : "ตั้งค่าให้ครบแล้วอนุมัติจากปุ่ม “อนุมัติ” ในรายการเครื่องที่รออนุมัติ"}
              </p>
            </div>
          </div>
          <div className="flex justify-end">
            <Button
              type="button"
              variant="destructive"
              size="sm"
              onClick={() => setRejectOpen(true)}
              disabled={disabled}
            >
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
  draft,
  onChange,
  submitted,
  moonlightInvalid,
  sunshineInvalid,
  vncInvalid,
  disabled,
}: {
  machine: Machine;
  draft: SettingsDraft;
  onChange: (patch: Partial<SettingsDraft>) => void;
  submitted: boolean;
  moonlightInvalid: boolean;
  sunshineInvalid: boolean;
  vncInvalid: boolean;
  disabled: boolean;
}) {
  const [check, setCheck] = useState<NetworkCheck | null>(null);
  const [message, setMessage] = useState("");
  const checkRun = useRef(0);

  function updateVncDraft(value: string) {
    checkRun.current += 1;
    onChange({ vncDraft: value });
    setCheck(null);
    setMessage("");
  }

  async function checkVncTarget() {
    const targetAddress = getVncTargetForExplicitSave(draft.vncDraft);
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

  return (
    <div className="flex flex-col gap-6">
      <FieldGroup className="gap-4">
        <div className="grid gap-4 sm:grid-cols-2">
          <Field data-invalid={submitted && moonlightInvalid}>
            <FieldLabel htmlFor="machine-moonlight-port">Moonlight port</FieldLabel>
            <Input
              id="machine-moonlight-port"
              type="number"
              min={1}
              max={65535}
              step={1}
              inputMode="numeric"
              value={draft.moonlightPort}
              aria-invalid={submitted && moonlightInvalid}
              disabled={disabled}
              onChange={(event) => onChange({ moonlightPort: event.currentTarget.value })}
            />
            {submitted && moonlightInvalid ? (
              <FieldError>{portErrorText}</FieldError>
            ) : (
              <FieldDescription>พอร์ต GameStream ที่ Moonlight ใช้</FieldDescription>
            )}
          </Field>
          <Field data-invalid={submitted && sunshineInvalid}>
            <FieldLabel htmlFor="machine-sunshine-port">Management port</FieldLabel>
            <Input
              id="machine-sunshine-port"
              type="number"
              min={1}
              max={65535}
              step={1}
              inputMode="numeric"
              value={draft.sunshinePort}
              aria-invalid={submitted && sunshineInvalid}
              disabled={disabled}
              onChange={(event) => onChange({ sunshinePort: event.currentTarget.value })}
            />
            {submitted && sunshineInvalid ? (
              <FieldError>{portErrorText}</FieldError>
            ) : (
              <FieldDescription>พอร์ต HTTPS ของ Sunshine management</FieldDescription>
            )}
          </Field>
        </div>
        <Field data-invalid={submitted && vncInvalid}>
          <FieldLabel htmlFor="machine-vnc-target">IP เป้าหมาย VNC</FieldLabel>
          <div className="flex flex-col gap-2 sm:flex-row">
            <Input
              id="machine-vnc-target"
              value={draft.vncDraft}
              aria-invalid={submitted && vncInvalid}
              disabled={disabled}
              onChange={(event) => updateVncDraft(event.currentTarget.value)}
            />
            <Button
              type="button"
              variant="outline"
              className="shrink-0"
              disabled={disabled || check?.state === "checking"}
              onClick={() => void checkVncTarget()}
            >
              {check?.state === "checking" ? "กำลังตรวจสอบ VNC…" : "ตรวจสอบ VNC"}
            </Button>
          </div>
          {submitted && vncInvalid ? (
            <FieldError>{vncErrorText}</FieldError>
          ) : (
            <FieldDescription>
              เว้นว่างได้หากยังไม่ต้องการเปิด VNC · รับเฉพาะ IPv4 LAN ส่วนตัว (10/8, 172.16/12, 192.168/16)
              หรือ IPv6 ULA
            </FieldDescription>
          )}
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
    </div>
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
  mode = "settings",
  actorId,
  onOpenChange,
  onSave,
  onReject,
  onUpdated,
  onApprove,
}: {
  machine: Machine | null;
  open: boolean;
  mode?: MachineSettingsMode;
  actorId: string;
  onOpenChange: (open: boolean) => void;
  onSave: (machine: Machine, input: MachineMetadataInput) => Promise<void>;
  onReject: (machine: Machine) => Promise<void>;
  onUpdated: () => Promise<boolean>;
  onApprove?: (machine: Machine) => Promise<void>;
}) {
  const [tab, setTab] = useState("general");
  const [draft, setDraft] = useState<SettingsDraft>(() => draftFromMachine(machine));
  const [submitted, setSubmitted] = useState(false);
  const [saving, setSaving] = useState(false);
  const [saveError, setSaveError] = useState("");
  const [confirmOpen, setConfirmOpen] = useState(false);
  const [approving, setApproving] = useState(false);
  const [approveError, setApproveError] = useState("");

  const name = machine?.displayName || machine?.machineId || "";
  const fingerprint = machine?.publicKeyFingerprint ?? null;
  const approveMode = mode === "approve";
  const trimmedName = draft.displayName.trim();
  const nameError = !trimmedName
    ? "กรอกชื่อที่แสดง"
    : trimmedName.length > 128
      ? "ชื่อต้องไม่เกิน 128 ตัวอักษร"
      : "";
  const priceValue = Number(draft.price);
  const priceError =
    Number.isSafeInteger(priceValue) && priceValue >= 1
      ? ""
      : "ราคาต้องเป็นจำนวนเต็มบาทตั้งแต่ 1 บาท";
  const moonlightPort = parsePort(draft.moonlightPort);
  const sunshinePort = parsePort(draft.sunshinePort);
  const vncTarget = draft.vncDraft.trim()
    ? getVncTargetForExplicitSave(draft.vncDraft)
    : null;
  const vncInvalid = draft.vncDraft.trim().length > 0 && vncTarget === null;

  function patchDraft(patch: Partial<SettingsDraft>) {
    setDraft((current) => ({ ...current, ...patch }));
  }

  async function persistDraft(): Promise<boolean> {
    setSubmitted(true);
    setSaveError("");
    if (!machine || saving) return false;
    if (nameError || priceError) {
      setTab("general");
      return false;
    }
    if (moonlightPort === null || sunshinePort === null || vncInvalid) {
      setTab("network");
      return false;
    }
    setSaving(true);
    try {
      await onSave(machine, {
        displayName: trimmedName,
        hourlyPriceMinorUnits: priceValue * 100,
        moonlightPort,
        sunshinePort,
        vncLocalIp: vncTarget,
        parsecEnabled: draft.parsecEnabled,
        availability: draft.availability,
      });
      return true;
    } catch (cause) {
      setSaveError(errorMessage(cause, "บันทึกการตั้งค่าไม่สำเร็จ"));
      return false;
    } finally {
      setSaving(false);
    }
  }

  async function primaryAction() {
    if (approveMode) {
      const saved = await persistDraft();
      if (!saved) return;
      setApproveError("");
      setConfirmOpen(true);
      return;
    }
    await persistDraft();
  }

  async function approve() {
    if (!machine || !onApprove || approving) return;
    if (!fingerprint) {
      setApproveError("Agent ยังไม่รายงาน fingerprint จึงยังอนุมัติไม่ได้");
      return;
    }
    setApproving(true);
    setApproveError("");
    try {
      await onApprove(machine);
      setConfirmOpen(false);
    } catch (cause) {
      setApproveError(errorMessage(cause, "อนุมัติเครื่องไม่สำเร็จ"));
    } finally {
      setApproving(false);
    }
  }

  function handleOpenChange(next: boolean) {
    if (!next && (saving || approving || confirmOpen)) return;
    onOpenChange(next);
  }

  return (
    <>
      <Dialog open={open} onOpenChange={handleOpenChange}>
        <DialogContent className="max-h-[85dvh] overflow-y-auto sm:max-w-3xl">
          <DialogHeader>
            <DialogTitle>
              {machine
                ? approveMode
                  ? `ตั้งค่าและอนุมัติเครื่อง ${name}`
                  : `ตั้งค่าเครื่อง ${name}`
                : "ตั้งค่าเครื่อง"}
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
            <>
              {approveMode && !fingerprint ? (
                <Alert variant="destructive">
                  <ShieldAlert aria-hidden />
                  <AlertTitle>ยังอนุมัติไม่ได้</AlertTitle>
                  <AlertDescription>
                    Agent ยังไม่รายงาน fingerprint — เครื่องต้องลงทะเบียนและส่ง fingerprint ก่อนอนุมัติ
                  </AlertDescription>
                </Alert>
              ) : null}

              <Tabs value={tab} onValueChange={setTab} className="gap-6">
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
                  <GeneralTab
                    machine={machine}
                    draft={draft}
                    onChange={patchDraft}
                    submitted={submitted}
                    nameError={nameError}
                    priceError={priceError}
                    disabled={saving || approving}
                    approveMode={approveMode}
                    onReject={onReject}
                  />
                </TabsContent>
                <TabsContent value="network" keepMounted className="min-h-80">
                  <NetworkTab
                    machine={machine}
                    draft={draft}
                    onChange={patchDraft}
                    submitted={submitted}
                    moonlightInvalid={moonlightPort === null}
                    sunshineInvalid={sunshinePort === null}
                    vncInvalid={vncInvalid}
                    disabled={saving || approving}
                  />
                </TabsContent>
                <TabsContent value="agent" keepMounted className="min-h-80">
                  <AgentTab machine={machine} actorId={actorId} onUpdated={onUpdated} />
                </TabsContent>
                <TabsContent value="diagnostics" keepMounted className="min-h-80">
                  <MachineDiagnostics machine={machine} />
                </TabsContent>
              </Tabs>

              {saveError ? (
                <Alert variant="destructive" role="alert">
                  <AlertTitle>บันทึกการตั้งค่าไม่สำเร็จ</AlertTitle>
                  <AlertDescription>{saveError}</AlertDescription>
                </Alert>
              ) : null}

              {approveMode ? (
                <p className="text-xs text-muted-foreground">
                  กด “ยืนยัน” ระบบจะบันทึกการตั้งค่า แล้วเปิดหน้าต่างยืนยันอนุมัติ
                </p>
              ) : null}
            </>
          ) : null}

          <DialogFooter>
            <Button
              type="button"
              variant="outline"
              onClick={() => onOpenChange(false)}
              disabled={saving || approving}
            >
              {approveMode ? "ยกเลิก" : "ปิด"}
            </Button>
            <Button
              type="button"
              onClick={() => void primaryAction()}
              disabled={!machine || saving || (approveMode && !fingerprint)}
            >
              {saving ? (
                <Spinner data-icon="inline-start" />
              ) : approveMode ? (
                <ShieldCheck data-icon="inline-start" />
              ) : null}
              {saving ? "กำลังบันทึก…" : approveMode ? "ยืนยัน" : "บันทึกการตั้งค่า"}
            </Button>
          </DialogFooter>
        </DialogContent>
      </Dialog>

      <AlertDialog
        open={confirmOpen}
        onOpenChange={(next) => {
          if (!next && !approving) setConfirmOpen(false);
        }}
      >
        <AlertDialogContent size="sm">
          <AlertDialogHeader>
            <AlertDialogMedia>
              <ShieldCheck aria-hidden />
            </AlertDialogMedia>
            <AlertDialogTitle>ยืนยันอนุมัติเครื่อง {name}</AlertDialogTitle>
            <AlertDialogDescription>
              ตรวจสอบการตั้งค่าแล้วกด “อนุมัติ” — ระบบจะใช้ fingerprint ที่ Agent
              รายงานโดยอัตโนมัติ ไม่ต้องกรอกยืนยัน
            </AlertDialogDescription>
          </AlertDialogHeader>
          <dl className="grid grid-cols-[auto_1fr] gap-x-4 gap-y-2 rounded-2xl bg-muted/50 p-3 text-sm">
            <dt className="text-muted-foreground">Machine ID</dt>
            <dd className="truncate font-mono text-xs">{machine?.machineId ?? "—"}</dd>
            <dt className="text-muted-foreground">ชื่อที่แสดง</dt>
            <dd className="truncate">{trimmedName || "—"}</dd>
            <dt className="text-muted-foreground">ราคา/ชม.</dt>
            <dd className="tabular-nums">
              {Number.isSafeInteger(priceValue) && priceValue >= 1
                ? formatBalance(priceValue * 100)
                : "—"}
            </dd>
            <dt className="text-muted-foreground">Moonlight port</dt>
            <dd className="tabular-nums">{draft.moonlightPort || "—"}</dd>
            <dt className="text-muted-foreground">Management port</dt>
            <dd className="tabular-nums">{draft.sunshinePort || "—"}</dd>
            <dt className="text-muted-foreground">VNC target</dt>
            <dd className="truncate">{vncTarget ?? "ยังไม่ตั้งค่า"}</dd>
            <dt className="text-muted-foreground">Parsec</dt>
            <dd>{draft.parsecEnabled ? "เปิด" : "ปิด"}</dd>
          </dl>
          {approveError ? (
            <Alert variant="destructive">
              <AlertTitle>อนุมัติเครื่องไม่สำเร็จ</AlertTitle>
              <AlertDescription>{approveError}</AlertDescription>
            </Alert>
          ) : null}
          <AlertDialogFooter>
            <AlertDialogCancel disabled={approving}>กลับไปแก้ไข</AlertDialogCancel>
            <AlertDialogAction
              variant="success"
              disabled={approving || !fingerprint}
              onClick={() => void approve()}
            >
              {approving ? (
                <Spinner data-icon="inline-start" />
              ) : (
                <ShieldCheck data-icon="inline-start" />
              )}
              อนุมัติ
            </AlertDialogAction>
          </AlertDialogFooter>
        </AlertDialogContent>
      </AlertDialog>
    </>
  );
}
