"use client";

import { useState } from "react";
import { ShieldCheck } from "lucide-react";
import { Alert, AlertDescription, AlertTitle } from "@/components/ui/alert";
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
import { Spinner } from "@/components/ui/spinner";
import type { Machine } from "@/lib/machine-contracts";
import { formatRamBytes, formatSpec } from "@/lib/machine-specs";

export function ApproveMachineDialog({
  machine,
  open,
  onOpenChange,
  onConfirm,
}: {
  machine: Machine | null;
  open: boolean;
  onOpenChange: (open: boolean) => void;
  onConfirm: (fingerprint: string) => Promise<void>;
}) {
  const [fingerprint, setFingerprint] = useState("");
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState("");
  const expected = machine?.publicKeyFingerprint ?? "";
  const typed = fingerprint.trim();
  const matched = Boolean(expected) && typed === expected;

  async function submit() {
    if (!machine || !matched || busy) return;
    setBusy(true);
    setError("");
    try {
      await onConfirm(typed);
    } catch (cause) {
      setError(cause instanceof Error ? cause.message : "อนุมัติเครื่องไม่สำเร็จ");
    } finally {
      setBusy(false);
    }
  }

  return (
    <Dialog
      open={open}
      onOpenChange={(next) => {
        if (!next && busy) return;
        onOpenChange(next);
      }}
    >
      <DialogContent className="sm:max-w-lg">
        <DialogHeader>
          <DialogTitle>อนุมัติเครื่อง {machine?.displayName || machine?.machineId || ""}</DialogTitle>
          <DialogDescription>
            ตรวจ fingerprint และข้อมูลเครื่องให้ตรงกับเครื่องจริงก่อนอนุมัติ
          </DialogDescription>
        </DialogHeader>
        {machine ? (
          <div className="flex flex-col gap-4">
            <dl className="grid gap-3 rounded-2xl bg-muted/50 p-4 text-sm sm:grid-cols-2">
              {[
                ["Machine ID", machine.machineId, true],
                ["Hostname", formatSpec(machine.hostname), false],
                ["IP ที่ Agent รายงาน", machine.localIp || "—", true],
                ["CPU", formatSpec(machine.cpu), false],
                ["GPU", formatSpec(machine.gpu), false],
                ["RAM", formatRamBytes(machine.ramBytes), false],
              ].map(([label, value, mono]) => (
                <div key={String(label)} className="flex min-w-0 flex-col gap-0.5">
                  <dt className="text-xs text-muted-foreground">{label}</dt>
                  <dd className={mono ? "truncate font-mono text-xs font-medium" : "truncate font-medium"}>
                    {value}
                  </dd>
                </div>
              ))}
            </dl>
            <div className="flex flex-col gap-1 rounded-2xl border border-status-busy/30 bg-status-busy/10 p-4">
              <span className="text-xs font-medium text-status-busy">Public key fingerprint</span>
              <code className="break-all font-mono text-xs">
                {expected || "Agent ยังไม่รายงาน fingerprint"}
              </code>
            </div>
            <FieldGroup>
              <Field data-invalid={Boolean(typed) && !matched}>
                <FieldLabel htmlFor="approve-fingerprint">พิมพ์ fingerprint เพื่อยืนยัน</FieldLabel>
                <Input
                  id="approve-fingerprint"
                  className="font-mono text-xs"
                  autoComplete="off"
                  spellCheck={false}
                  value={fingerprint}
                  disabled={!expected || busy}
                  aria-invalid={Boolean(typed) && !matched}
                  onChange={(event) => setFingerprint(event.currentTarget.value)}
                />
                <FieldDescription>ต้องตรงกับ fingerprint ด้านบนทุกตัวอักษร</FieldDescription>
              </Field>
            </FieldGroup>
            {error ? (
              <Alert variant="destructive">
                <AlertTitle>อนุมัติเครื่องไม่สำเร็จ</AlertTitle>
                <AlertDescription>{error}</AlertDescription>
              </Alert>
            ) : null}
          </div>
        ) : null}
        <DialogFooter>
          <Button type="button" variant="outline" disabled={busy} onClick={() => onOpenChange(false)}>
            ยกเลิก
          </Button>
          <Button type="button" disabled={!matched || busy} onClick={() => void submit()}>
            {busy ? <Spinner data-icon="inline-start" /> : <ShieldCheck data-icon="inline-start" />}
            ยืนยันอนุมัติ
          </Button>
        </DialogFooter>
      </DialogContent>
    </Dialog>
  );
}
