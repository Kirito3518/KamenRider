"use client";

import { useRef, useState, type FormEvent, type RefObject } from "react";
import Link from "next/link";
import { AlertTriangle, Minus, Plus, Wallet } from "lucide-react";
import toast from "react-hot-toast";
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
import {
  Field,
  FieldDescription,
  FieldError,
  FieldGroup,
  FieldLabel,
} from "@/components/ui/field";
import {
  InputGroup,
  InputGroupAddon,
  InputGroupButton,
  InputGroupInput,
} from "@/components/ui/input-group";
import { Spinner } from "@/components/ui/spinner";
import { leaseErrorMessage } from "@/components/my-machines/lease-api";
import type { Lease } from "@/lib/machine-contracts";
import { formatBalance } from "@/lib/money";
import { formatRentalDateTime } from "@/lib/rental-history";

const maxExtensionHours = 24;

export function ExtendRentalDialog({
  lease,
  balanceSatang,
  open,
  onClose,
  onExtend,
  returnFocusRef,
}: {
  lease: Lease;
  balanceSatang: number;
  open: boolean;
  onClose: () => void;
  onExtend: (hours: number, idempotencyKey: string) => Promise<void>;
  returnFocusRef: RefObject<HTMLButtonElement | null>;
}) {
  const [hours, setHours] = useState("1");
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState("");
  const requestKey = useRef<string | null>(null);
  const amountInputRef = useRef<HTMLInputElement>(null);

  const numericHours = Number(hours);
  const validHours =
    Number.isSafeInteger(numericHours) &&
    numericHours >= 1 &&
    numericHours <= maxExtensionHours;
  const showError = hours !== "" && !validHours;
  const hourlyPriceSatang = lease.payment?.hourlyPriceSatang ?? null;
  const totalSatang =
    hourlyPriceSatang !== null && validHours
      ? hourlyPriceSatang * numericHours
      : null;
  const affordable = totalSatang === null || totalSatang <= balanceSatang;
  const canExtend =
    lease.state === "active" &&
    lease.pendingRevision === null &&
    lease.payment?.extendable === true;

  function updateHours(value: string) {
    setHours(value);
    // A new duration is a new request; reusing the previous key would conflict.
    requestKey.current = null;
    setError("");
  }

  async function submit(event: FormEvent<HTMLFormElement>) {
    event.preventDefault();
    if (!validHours || !canExtend || !affordable || busy) return;
    const key = requestKey.current ?? crypto.randomUUID();
    requestKey.current = key;
    setBusy(true);
    setError("");
    try {
      await onExtend(numericHours, key);
      toast.success("ต่อเวลาแล้ว รอเครื่องยืนยันการเปลี่ยนแปลง");
      onClose();
    } catch (cause) {
      const text = leaseErrorMessage(cause);
      setError(text);
      toast.error(text);
    } finally {
      setBusy(false);
    }
  }

  const expiresAtMs = lease.expiresAtUtc
    ? Date.parse(lease.expiresAtUtc)
    : Number.NaN;
  const newExpiry =
    validHours && Number.isFinite(expiresAtMs)
      ? new Date(expiresAtMs + numericHours * 3_600_000).toISOString()
      : null;

  return (
    <Dialog
      open={open}
      onOpenChange={(next) => {
        if (!next) onClose();
      }}
      onOpenChangeComplete={(next) => {
        if (next) return;
        setHours("1");
        setBusy(false);
        setError("");
        requestKey.current = null;
      }}
    >
      <DialogContent data-lenis-prevent initialFocus={amountInputRef} finalFocus={returnFocusRef}>
        <DialogHeader className="pr-8">
          <DialogTitle>
            ต่อเวลา {lease.machineDisplayName || lease.machineId}
          </DialogTitle>
          <DialogDescription>
            หักค่าเช่าตามชั่วโมงที่ต่อทันที และคืนเงินตามเวลาที่ไม่ได้ใช้เมื่อสิ้นสุดการเช่า
          </DialogDescription>
        </DialogHeader>

        <form className="flex flex-col gap-4" onSubmit={(event) => void submit(event)}>
          {!canExtend ? (
            <Alert variant="destructive">
              <AlertTriangle aria-hidden />
              <AlertTitle>ต่อเวลาไม่ได้ในขณะนี้</AlertTitle>
              <AlertDescription>
                {lease.pendingRevision !== null
                  ? "กำลังรอเครื่องยืนยันการต่อเวลาครั้งก่อน กรุณารอสักครู่แล้วรีเฟรช"
                  : lease.payment
                    ? "รายการเช่านี้ไม่สามารถต่อเวลาได้"
                    : "รายการนี้ไม่มีข้อมูลการชำระเงิน จึงไม่สามารถต่อเวลาได้"}
              </AlertDescription>
            </Alert>
          ) : (
            <>
              <FieldGroup className="gap-4">
                <Field data-invalid={showError}>
                  <FieldLabel htmlFor="extend-hours">
                    ระยะเวลา (ชั่วโมง)
                  </FieldLabel>
                  <InputGroup>
                    <InputGroupAddon>
                      <InputGroupButton
                        size="icon-sm"
                        variant="outline"
                        aria-label="ลดเวลา 1 ชั่วโมง"
                        disabled={!validHours || numericHours <= 1 || busy}
                        onClick={() =>
                          updateHours(String(numericHours - 1))
                        }
                      >
                        <Minus />
                      </InputGroupButton>
                    </InputGroupAddon>
                    <InputGroupInput
                      ref={amountInputRef}
                      id="extend-hours"
                      type="number"
                      inputMode="numeric"
                      min={1}
                      max={maxExtensionHours}
                      step={1}
                      required
                      value={hours}
                      disabled={busy}
                      onChange={(event) => updateHours(event.target.value)}
                      onBlur={() => {
                        if (hours === "") updateHours("1");
                      }}
                      aria-invalid={showError}
                      aria-describedby={
                        showError ? "extend-hours-error" : undefined
                      }
                      className="text-center tabular-nums [appearance:textfield] [&::-webkit-inner-spin-button]:appearance-none [&::-webkit-outer-spin-button]:appearance-none"
                    />
                    <InputGroupAddon align="inline-end">
                      <InputGroupButton
                        size="icon-sm"
                        variant="default"
                        aria-label="เพิ่มเวลา 1 ชั่วโมง"
                        disabled={
                          !validHours ||
                          numericHours >= maxExtensionHours ||
                          busy
                        }
                        onClick={() =>
                          updateHours(
                            String(
                              Math.min(
                                (validHours ? numericHours : 0) + 1,
                                maxExtensionHours,
                              ),
                            ),
                          )
                        }
                      >
                        <Plus />
                      </InputGroupButton>
                    </InputGroupAddon>
                  </InputGroup>
                  {showError ? (
                    <FieldError id="extend-hours-error">
                      กรอกจำนวนชั่วโมงเต็ม 1–{maxExtensionHours} ชั่วโมง
                    </FieldError>
                  ) : (
                    <FieldDescription>
                      ต่อเวลาได้ครั้งละ 1–{maxExtensionHours} ชั่วโมง
                    </FieldDescription>
                  )}
                </Field>
              </FieldGroup>

              <div className="flex flex-col gap-2 rounded-2xl border bg-muted/40 p-3 text-sm">
                <div className="flex items-center justify-between gap-3">
                  <span className="text-muted-foreground">ค่าใช้จ่ายครั้งนี้</span>
                  <span className="font-semibold tabular-nums">
                    {totalSatang === null ? "—" : formatBalance(totalSatang)}
                  </span>
                </div>
                <div className="flex items-center justify-between gap-3">
                  <span className="flex items-center gap-2 text-muted-foreground">
                    <Wallet aria-hidden className="size-4" />
                    ยอดเงินคงเหลือ
                  </span>
                  <span className="font-medium tabular-nums">
                    {formatBalance(balanceSatang)}
                  </span>
                </div>
                {newExpiry ? (
                  <div className="flex items-center justify-between gap-3">
                    <span className="text-muted-foreground">เวลาใหม่จะสิ้นสุด</span>
                    <span className="font-medium">
                      {formatRentalDateTime(newExpiry)}
                    </span>
                  </div>
                ) : null}
              </div>

              {!affordable ? (
                <Alert variant="destructive">
                  <AlertTriangle aria-hidden />
                  <AlertTitle>ยอดเงินไม่เพียงพอ</AlertTitle>
                  <AlertDescription>
                    ต้องการ {formatBalance(totalSatang ?? 0)} แต่คงเหลือ{" "}
                    {formatBalance(balanceSatang)}
                    <Button
                      variant="outline"
                      size="sm"
                      className="mt-2"
                      nativeButton={false}
                      render={<Link href="/topup" />}
                    >
                      <Wallet data-icon="inline-start" />
                      ไปหน้าเติมเงิน
                    </Button>
                  </AlertDescription>
                </Alert>
              ) : null}

              {error ? (
                <Alert variant="destructive" role="alert">
                  <AlertTriangle aria-hidden />
                  <AlertTitle>ต่อเวลาไม่สำเร็จ</AlertTitle>
                  <AlertDescription>{error}</AlertDescription>
                </Alert>
              ) : null}

              <Button
                type="submit"
                className="w-full"
                disabled={
                  busy || !validHours || !affordable || !canExtend
                }
              >
                {busy ? <Spinner data-icon="inline-start" /> : null}
                {busy ? "กำลังต่อเวลา…" : "ยืนยันต่อเวลา"}
              </Button>
            </>
          )}
        </form>

        <DialogFooter>
          <Button variant="outline" onClick={onClose} disabled={busy}>
            ปิด
          </Button>
        </DialogFooter>
      </DialogContent>
    </Dialog>
  );
}
