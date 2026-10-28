"use client";

import { useRef, useState, type RefObject } from "react";
import { Info, Minus, Plus } from "lucide-react";
import { Button } from "@/components/ui/button";
import { Dialog, DialogContent, DialogDescription, DialogHeader, DialogTitle } from "@/components/ui/dialog";
import { Field, FieldError, FieldGroup, FieldLabel } from "@/components/ui/field";
import { InputGroup, InputGroupAddon, InputGroupButton, InputGroupInput } from "@/components/ui/input-group";
import { Alert, AlertDescription } from "@/components/ui/alert";
import { type RentalMachine } from "@/lib/rental-machines";

export function RentalDialog({ machine, onClose, returnFocusRef }: { machine: RentalMachine | null; onClose: () => void; returnFocusRef: RefObject<HTMLButtonElement | null> }) {
  const [hours, setHours] = useState("1");
  const [preview, setPreview] = useState(false);
  const durationRef = useRef<HTMLInputElement>(null);
  const numericHours = Number(hours);
  const validHours = Number.isSafeInteger(numericHours) && numericHours >= 1 && Number.isSafeInteger(numericHours * (machine?.price ?? 1));
  const showError = hours !== "" && !validHours;

  function updateHours(value: string) {
    setHours(value);
    setPreview(false);
  }

  return (
    <Dialog open={!!machine} onOpenChange={open => { if (!open) onClose(); }} onOpenChangeComplete={open => { if (!open) { setHours("1"); setPreview(false); } }}>
      <DialogContent data-lenis-prevent className="max-h-[calc(100dvh-2rem)] overflow-y-auto rounded-2xl" initialFocus={durationRef} finalFocus={returnFocusRef}>
        <DialogHeader className="pr-8">
          <DialogTitle>เช่าเครื่อง {machine?.id}</DialogTitle>
          <DialogDescription>เลือกเวลาที่ต้องการใช้งาน · ข้อมูลตัวอย่าง</DialogDescription>
        </DialogHeader>
        {machine && <>
          <dl className="grid grid-cols-[auto_1fr] gap-x-6 gap-y-3 text-sm">
            <dt className="text-muted-foreground">CPU</dt><dd>{machine.cpu}</dd>
            <dt className="text-muted-foreground">GPU</dt><dd>{machine.gpu}</dd>
            <dt className="text-muted-foreground">RAM / SSD</dt><dd>{machine.ram} GB / {machine.storage}</dd>
            <dt className="text-muted-foreground">ราคา</dt><dd>฿{machine.price} / ชั่วโมง</dd>
          </dl>
          <FieldGroup>
            <Field data-invalid={showError}>
              <FieldLabel htmlFor="rental-duration">ระยะเวลาเช่า (ชั่วโมง)</FieldLabel>
              <InputGroup>
                <InputGroupAddon>
                  <InputGroupButton size="icon-sm" variant="outline" aria-label="ลดเวลาเช่า 1 ชั่วโมง" disabled={!validHours || numericHours <= 1} onClick={() => updateHours(String(numericHours - 1))}><Minus /></InputGroupButton>
                </InputGroupAddon>
                <InputGroupInput ref={durationRef} id="rental-duration" type="number" inputMode="numeric" min={1} step={1} required value={hours} onChange={event => updateHours(event.target.value)} onBlur={() => { if (hours === "") updateHours("1"); }} aria-invalid={showError} aria-describedby={showError ? "rental-duration-error" : undefined} className="text-center tabular-nums [appearance:textfield] [&::-webkit-inner-spin-button]:appearance-none [&::-webkit-outer-spin-button]:appearance-none" />
                <InputGroupAddon align="inline-end">
                  <InputGroupButton size="icon-sm" variant="default" aria-label="เพิ่มเวลาเช่า 1 ชั่วโมง" disabled={validHours && !Number.isSafeInteger((numericHours + 1) * machine.price)} onClick={() => updateHours(String(validHours ? numericHours + 1 : 1))}><Plus /></InputGroupButton>
                </InputGroupAddon>
              </InputGroup>
              {showError && <FieldError id="rental-duration-error">กรอกจำนวนชั่วโมงที่ถูกต้อง เป็นจำนวนเต็มตั้งแต่ 1 ขึ้นไป</FieldError>}
            </Field>
          </FieldGroup>
          <div className="flex items-center justify-between gap-4"><span className="text-sm text-muted-foreground">ยอดรวม{validHours ? ` ${numericHours} ชั่วโมง` : ""}</span><p className="text-3xl font-bold text-primary" aria-live="polite">{validHours ? `฿${machine.price * numericHours}` : "—"}</p></div>
          <Button disabled={!validHours} onClick={() => { if (validHours) setPreview(true); }}>ยืนยันการเช่า</Button>
          {preview && <Alert role="status"><Info /><AlertDescription>นี่เป็นตัวอย่าง UI ยังไม่ได้จองเครื่องหรือเรียกเก็บเงิน</AlertDescription></Alert>}
        </>}
      </DialogContent>
    </Dialog>
  );
}
