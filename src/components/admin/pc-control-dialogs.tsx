"use client";

import { useEffect, useState } from "react";
import { KeyRound, Minus, Plus, PowerOff, RefreshCw, Unlink, XCircle } from "lucide-react";
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
import { Button } from "@/components/ui/button";
import {
  Dialog,
  DialogContent,
  DialogDescription,
  DialogFooter,
  DialogHeader,
  DialogTitle,
} from "@/components/ui/dialog";
import { Field, FieldDescription, FieldGroup, FieldLabel } from "@/components/ui/field";
import { Input } from "@/components/ui/input";
import { Select, SelectContent, SelectGroup, SelectItem, SelectTrigger, SelectValue } from "@/components/ui/select";
import { Skeleton } from "@/components/ui/skeleton";
import { Spinner } from "@/components/ui/spinner";
import { ToggleGroup, ToggleGroupItem } from "@/components/ui/toggle-group";
import { formatPcControlDateTime, type AdminPairingOffer, type PcControlMachine } from "@/lib/pc-control";

async function readJson(response: Response) {
  return (await response.json().catch(() => ({}))) as Record<string, unknown>;
}

export function PcControlConfirmDialog({
  open,
  busy,
  kind,
  machine,
  onClose,
  onConfirm,
}: {
  open: boolean;
  busy: boolean;
  kind: "powerOff" | "cancel" | "restartSunshine" | "unpairSunshine";
  machine: PcControlMachine | null;
  onClose: () => void;
  onConfirm: () => void;
}) {
  const powerOff = kind === "powerOff";
  const sunshine = kind === "restartSunshine" || kind === "unpairSunshine";
  const restartSunshine = kind === "restartSunshine";
  const name = machine?.displayName || machine?.machineId;
  const interruptingRental = sunshine && machine?.lease !== null;
  return (
    <AlertDialog open={open} onOpenChange={(next) => { if (!next) onClose(); }}>
      <AlertDialogContent size="sm">
        <AlertDialogHeader>
          <AlertDialogTitle>
            {powerOff
              ? "ยืนยันปิดเครื่อง"
              : kind === "cancel"
                ? "ยืนยันยกเลิกการเช่า"
                : restartSunshine
                  ? "ยืนยันรีสตาร์ท Sunshine"
                  : "ยืนยันยกเลิกการจับคู่ Sunshine"}
          </AlertDialogTitle>
          <AlertDialogDescription>
            {powerOff
              ? `ปิดเครื่อง ${name} ทันทีและสิ้นสุดการเช่าปัจจุบัน โดยไม่คืนเงิน`
              : kind === "cancel"
                ? `ยกเลิกการเช่าของ ${name} โดยไม่คืนเงิน เครื่องจะหยุดให้บริการหลัง Agent ยืนยัน`
                : restartSunshine
                  ? `รีสตาร์ท Sunshine ของ ${name} ระบบสตรีมจะหยุดชั่วครู่${interruptingRental ? " และผู้เช่าปัจจุบันจะถูกตัดการเชื่อมต่อ" : ""}`
                  : `ล้างเครื่องเล่นที่จับคู่ไว้ทั้งหมดของ ${name} แล้วรีสตาร์ท Sunshine${interruptingRental ? " ผู้เช่าปัจจุบันจะถูกตัดการเชื่อมต่อ" : ""}`}
          </AlertDialogDescription>
        </AlertDialogHeader>
        <AlertDialogFooter>
          <AlertDialogCancel disabled={busy}>ไม่ใช่ตอนนี้</AlertDialogCancel>
          <AlertDialogAction
            variant={powerOff || kind === "cancel" ? "destructive" : "busy"}
            onClick={onConfirm}
            disabled={busy}
          >
            {busy ? (
              <Spinner data-icon="inline-start" />
            ) : powerOff ? (
              <PowerOff data-icon="inline-start" />
            ) : kind === "cancel" ? (
              <XCircle data-icon="inline-start" />
            ) : restartSunshine ? (
              <RefreshCw data-icon="inline-start" />
            ) : (
              <Unlink data-icon="inline-start" />
            )}
            {powerOff
              ? "ปิดเครื่องทันที"
              : kind === "cancel"
                ? "ยกเลิกการเช่า"
                : restartSunshine
                  ? "รีสตาร์ท Sunshine"
                  : "ยกเลิกการจับคู่"}
          </AlertDialogAction>
        </AlertDialogFooter>
      </AlertDialogContent>
    </AlertDialog>
  );
}

export function PcControlPairDialog({
  machine,
  onClose,
  onCompleted,
}: {
  machine: PcControlMachine | null;
  onClose: () => void;
  onCompleted: () => void;
}) {
  const [offer, setOffer] = useState<AdminPairingOffer | null>(null);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState("");
  const [pairingId, setPairingId] = useState("");
  const [pin, setPin] = useState("");
  const [submitting, setSubmitting] = useState(false);
  const [commandId, setCommandId] = useState("");
  const [status, setStatus] = useState("");

  useEffect(() => {
    if (!machine) return;
    const controller = new AbortController();
    const loadTimer = window.setTimeout(() => {
      void (async () => {
        try {
          const response = await fetch(`/api/admin/pc-control/${encodeURIComponent(machine.machineId)}/pairing`, {
            cache: "no-store",
            signal: controller.signal,
          });
          const payload = await readJson(response);
          if (!response.ok) throw new Error(typeof payload.error === "string" ? payload.error : "โหลดข้อมูลจับคู่ไม่สำเร็จ");
          setOffer(payload as unknown as AdminPairingOffer | null);
        } catch (cause) {
          if (!controller.signal.aborted)
            setError(cause instanceof Error ? cause.message : "โหลดข้อมูลจับคู่ไม่สำเร็จ");
        } finally {
          if (!controller.signal.aborted) setLoading(false);
        }
      })();
    }, 0);
    return () => {
      window.clearTimeout(loadTimer);
      controller.abort();
    };
  }, [machine]);

  useEffect(() => {
    if (!machine || !commandId) return;
    if (status === "succeeded" || status === "failed" || status === "expired") return;
    const timer = window.setInterval(() => {
      void (async () => {
        try {
          const response = await fetch(
            `/api/admin/pc-control/${encodeURIComponent(machine.machineId)}/pairing/${encodeURIComponent(commandId)}`,
            { cache: "no-store" },
          );
          const payload = await readJson(response);
          if (!response.ok) return;
          const nextStatus = typeof payload.status === "string" ? payload.status : status;
          setStatus(nextStatus);
          if (nextStatus === "succeeded") onCompleted();
        } catch {
          // Keep polling; the dialog remains usable.
        }
      })();
    }, 2_000);
    return () => window.clearInterval(timer);
  }, [commandId, machine, onCompleted, status]);

  async function submit() {
    if (!machine || !pairingId || !/^[0-9]{4}$/.test(pin)) return;
    setSubmitting(true);
    setError("");
    try {
      const response = await fetch(`/api/admin/pc-control/${encodeURIComponent(machine.machineId)}/pairing`, {
        method: "POST",
        cache: "no-store",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify({ pairingId, pin, idempotencyKey: crypto.randomUUID() }),
      });
      const payload = await readJson(response);
      if (!response.ok) throw new Error(typeof payload.error === "string" ? payload.error : "ส่ง PIN ไม่สำเร็จ");
      if (typeof payload.commandId === "string") setCommandId(payload.commandId);
      setStatus(typeof payload.status === "string" ? payload.status : "queued");
      setPin("");
    } catch (cause) {
      setError(cause instanceof Error ? cause.message : "ส่ง PIN ไม่สำเร็จ");
    } finally {
      setSubmitting(false);
    }
  }

  return (
    <Dialog open={machine !== null} onOpenChange={(next) => { if (!next) onClose(); }}>
      <DialogContent>
        <DialogHeader>
          <DialogTitle>เชื่อมต่อพิน {machine?.displayName || machine?.machineId}</DialogTitle>
          <DialogDescription>
            ให้ลูกค้าเริ่ม Pair ใน Moonlight แล้วแอดมินกรอก PIN 4 หลักเพื่อส่งแทน
          </DialogDescription>
        </DialogHeader>
        {loading ? <Skeleton className="h-24 w-full rounded-2xl" /> : null}
        {error ? (
          <Alert variant="destructive">
            <AlertTitle>ทำรายการไม่สำเร็จ</AlertTitle>
            <AlertDescription>{error}</AlertDescription>
          </Alert>
        ) : null}
        {!loading && !error && !offer ? (
          <Alert>
            <AlertTitle>ยังไม่มีช่องจับคู่</AlertTitle>
            <AlertDescription>ให้ลูกค้าเริ่ม Pair ใน Moonlight ภายในหน้าต่างเวลาก่อน แล้วกดลองใหม่</AlertDescription>
          </Alert>
        ) : null}
        {offer ? (
          <FieldGroup className="gap-4">
            <Field>
              <FieldLabel htmlFor="pairing-slot">ช่องจับคู่</FieldLabel>
              <Select value={pairingId} onValueChange={(value) => setPairingId(String(value ?? ""))}>
                <SelectTrigger id="pairing-slot" className="w-full">
                  <SelectValue placeholder="เลือกช่องจับคู่" />
                </SelectTrigger>
                <SelectContent>
                  <SelectGroup>
                    {offer.pairingIds.map((id) => (
                      <SelectItem key={id} value={id}>
                        {id === "foundation-sunshine-single-pending-slot" ? "Sunshine ของเครื่องนี้" : id}
                      </SelectItem>
                    ))}
                  </SelectGroup>
                </SelectContent>
              </Select>
              <FieldDescription>ใช้ได้ถึง {formatPcControlDateTime(offer.validUntilUtc)}</FieldDescription>
            </Field>
            <Field>
              <FieldLabel htmlFor="pairing-pin">PIN 4 หลัก</FieldLabel>
              <Input
                id="pairing-pin"
                inputMode="numeric"
                pattern="[0-9]{4}"
                maxLength={4}
                autoComplete="off"
                value={pin}
                onChange={(event) => setPin(event.target.value.replace(/\D/g, "").slice(0, 4))}
              />
            </Field>
          </FieldGroup>
        ) : null}
        {status ? (
          <Alert>
            <AlertTitle>สถานะการเชื่อมต่อ</AlertTitle>
            <AlertDescription>{status}</AlertDescription>
          </Alert>
        ) : null}
        <DialogFooter>
          <Button variant="outline" onClick={onClose} disabled={submitting}>
            ปิด
          </Button>
          <Button onClick={() => void submit()} disabled={!offer || !pairingId || pin.length !== 4 || submitting}>
            {submitting ? <Spinner data-icon="inline-start" /> : <KeyRound data-icon="inline-start" />}
            ส่ง PIN
          </Button>
        </DialogFooter>
      </DialogContent>
    </Dialog>
  );
}

export function PcControlTimeDialog({
  machine,
  busy,
  onClose,
  onSubmit,
}: {
  machine: PcControlMachine | null;
  busy: boolean;
  onClose: () => void;
  onSubmit: (deltaSeconds: number) => void;
}) {
  const [preset, setPreset] = useState("60");
  const [direction, setDirection] = useState("increase");
  const [minutes, setMinutes] = useState("");
  const [error, setError] = useState("");

  function submit() {
    let delta: number;
    if (preset) delta = Number(preset) * 60;
    else {
      const value = Number(minutes);
      if (!Number.isSafeInteger(value) || value < 1 || value > 86_400) {
        setError("กรอกจำนวนนาทีเป็นจำนวนเต็ม 1–1440");
        return;
      }
      delta = (direction === "decrease" ? -1 : 1) * value * 60;
    }
    setError("");
    onSubmit(delta);
  }

  return (
    <Dialog open={machine !== null} onOpenChange={(next) => { if (!next) onClose(); }}>
      <DialogContent>
        <DialogHeader>
          <DialogTitle>กำหนดเวลา {machine?.displayName || machine?.machineId}</DialogTitle>
          <DialogDescription>ลดเวลาแล้วระบบจะคืนเงินตามเวลาที่ไม่ได้ใช้เมื่อสิ้นสุดการเช่า ส่วนการเพิ่มเวลาไม่มีค่าใช้จ่าย</DialogDescription>
        </DialogHeader>
        <FieldGroup className="gap-4">
          <Field>
            <FieldLabel>ตัวเลือกด่วน</FieldLabel>
            <ToggleGroup
              multiple={false}
              variant="outline"
              size="sm"
              value={preset ? [preset] : []}
              onValueChange={(values) => setPreset(values[0] ?? "")}
            >
              <ToggleGroupItem value="-60">
                <Minus data-icon="inline-start" />
                1 ชม.
              </ToggleGroupItem>
              <ToggleGroupItem value="-30">−30 นาที</ToggleGroupItem>
              <ToggleGroupItem value="30">+30 นาที</ToggleGroupItem>
              <ToggleGroupItem value="60">
                <Plus data-icon="inline-start" />
                1 ชม.
              </ToggleGroupItem>
            </ToggleGroup>
            <FieldDescription>เลือกตัวเลือกด่วน หรือเว้นว่างเพื่อกรอกจำนวนนาทีเอง</FieldDescription>
          </Field>
          <Field>
            <FieldLabel htmlFor="time-minutes">กำหนดเอง (นาที)</FieldLabel>
            <div className="flex items-center gap-2">
              <Input
                id="time-minutes"
                inputMode="numeric"
                value={minutes}
                onChange={(event) => {
                  setMinutes(event.target.value.replace(/\D/g, "").slice(0, 4));
                  setPreset("");
                }}
                placeholder="เช่น 45"
              />
              <ToggleGroup
                multiple={false}
                variant="outline"
                size="sm"
                value={[direction]}
                onValueChange={(values) => setDirection(values[0] ?? "increase")}
              >
                <ToggleGroupItem value="increase">เพิ่ม</ToggleGroupItem>
                <ToggleGroupItem value="decrease">ลด</ToggleGroupItem>
              </ToggleGroup>
            </div>
          </Field>
        </FieldGroup>
        {error ? (
          <Alert variant="destructive">
            <AlertTitle>ข้อมูลเวลาไม่ถูกต้อง</AlertTitle>
            <AlertDescription>{error}</AlertDescription>
          </Alert>
        ) : null}
        <DialogFooter>
          <Button variant="outline" onClick={onClose} disabled={busy}>
            ยกเลิก
          </Button>
          <Button onClick={submit} disabled={busy || (!preset && !minutes)}>
            {busy ? <Spinner data-icon="inline-start" /> : <XCircle data-icon="inline-start" />}
            ยืนยันเวลา
          </Button>
        </DialogFooter>
      </DialogContent>
    </Dialog>
  );
}
