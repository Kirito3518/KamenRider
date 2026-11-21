"use client";

import { useEffect, useRef, useState, type RefObject } from "react";
import { Check, Copy, KeyRound, RefreshCw } from "lucide-react";
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
import { Input } from "@/components/ui/input";
import {
  Select,
  SelectContent,
  SelectGroup,
  SelectItem,
  SelectTrigger,
  SelectValue,
} from "@/components/ui/select";
import { Spinner } from "@/components/ui/spinner";
import { leaseApi, leaseErrorMessage } from "@/components/my-machines/lease-api";
import type {
  Lease,
  Offer,
  PairingCommandStatus,
} from "@/lib/machine-contracts";
import { formatRentalDateTime } from "@/lib/rental-history";

const foundationSunshineSlot = "foundation-sunshine-single-pending-slot";
const offerPollIntervalMs = 2_500;
const commandPollIntervalMs = 2_000;

function pairingSlotLabel(id: string) {
  return id === foundationSunshineSlot ? "Sunshine ของเครื่องนี้" : id;
}

export function MoonlightDialog({
  lease,
  open,
  onClose,
  returnFocusRef,
}: {
  lease: Lease;
  open: boolean;
  onClose: () => void;
  returnFocusRef: RefObject<HTMLButtonElement | null>;
}) {
  const base = `/api/my-leases/${encodeURIComponent(lease.id)}/pair`;
  const address = lease.connection?.address ?? null;
  const stale = lease.connection?.stale === true;

  const [offer, setOffer] = useState<Offer>(null);
  const [offerLoading, setOfferLoading] = useState(true);
  const [offerError, setOfferError] = useState("");
  const [pairingId, setPairingId] = useState("");
  const [pin, setPin] = useState("");
  const [submitting, setSubmitting] = useState(false);
  const [commandId, setCommandId] = useState<string | null>(null);
  const [commandStatus, setCommandStatus] =
    useState<PairingCommandStatus["status"] | null>(null);
  const [message, setMessage] = useState("");
  const [copyState, setCopyState] = useState<"idle" | "copied">("idle");
  const [copyError, setCopyError] = useState("");
  const copyButtonRef = useRef<HTMLButtonElement>(null);
  const offerRequestInFlight = useRef(false);
  const commandRequestInFlight = useRef(false);
  const copyResetTimer = useRef<number | null>(null);

  const commandStarted = commandId !== null;
  const commandFinished =
    commandStatus === "succeeded" ||
    commandStatus === "failed" ||
    commandStatus === "expired";
  const pinValid = /^\d{4}$/.test(pin);
  const pinInvalid = pin.length > 0 && !pinValid;
  const canSubmit = Boolean(offer && pairingId && pinValid) && !submitting;

  useEffect(() => {
    return () => {
      if (copyResetTimer.current !== null)
        window.clearTimeout(copyResetTimer.current);
    };
  }, []);

  // Discover the pending Moonlight pairing slot automatically while the dialog is open
  // and no PIN command has been queued yet.
  useEffect(() => {
    if (!open || commandStarted) return;
    let cancelled = false;
    const load = async () => {
      if (offerRequestInFlight.current) return;
      offerRequestInFlight.current = true;
      try {
        const next = await leaseApi<Offer>(base);
        if (cancelled) return;
        setOffer(next);
        setOfferError("");
        setOfferLoading(false);
        setPairingId((current) => {
          const ids = next?.pairingIds ?? [];
          return current && ids.includes(current) ? current : (ids[0] ?? "");
        });
      } catch (error) {
        if (cancelled) return;
        setOfferError(leaseErrorMessage(error));
        setOfferLoading(false);
      } finally {
        offerRequestInFlight.current = false;
      }
    };
    void load();
    const timer = window.setInterval(() => void load(), offerPollIntervalMs);
    return () => {
      cancelled = true;
      window.clearInterval(timer);
    };
  }, [open, commandStarted, base]);

  // Poll the PairPin command until it reaches a terminal status.
  useEffect(() => {
    if (!open || !commandId || commandFinished) return;
    let cancelled = false;
    const check = async () => {
      if (commandRequestInFlight.current) return;
      commandRequestInFlight.current = true;
      try {
        const result = await leaseApi<PairingCommandStatus>(
          `${base}/${encodeURIComponent(commandId)}`,
        );
        if (cancelled) return;
        setCommandStatus(result.status);
        if (result.status === "succeeded") {
          setMessage("");
          toast.success("จับคู่ Moonlight สำเร็จ");
        } else if (result.status === "failed") {
          toast.error("จับคู่ Moonlight ไม่สำเร็จ");
        }
      } catch (error) {
        if (!cancelled) setMessage(leaseErrorMessage(error));
      } finally {
        commandRequestInFlight.current = false;
      }
    };
    void check();
    const timer = window.setInterval(() => void check(), commandPollIntervalMs);
    return () => {
      cancelled = true;
      window.clearInterval(timer);
    };
  }, [open, commandId, commandFinished, base]);

  async function copyAddress() {
    if (!address) return;
    try {
      if (!navigator.clipboard?.writeText)
        throw new Error("Clipboard API unavailable");
      await navigator.clipboard.writeText(address);
      setCopyError("");
      setCopyState("copied");
      toast.success("คัดลอกที่อยู่เชื่อมต่อแล้ว");
      if (copyResetTimer.current !== null)
        window.clearTimeout(copyResetTimer.current);
      copyResetTimer.current = window.setTimeout(
        () => setCopyState("idle"),
        2_000,
      );
    } catch {
      setCopyError("คัดลอกอัตโนมัติไม่ได้ กรุณาเลือกข้อความแล้วคัดลอกเอง");
      toast.error("คัดลอกที่อยู่ไม่สำเร็จ");
    }
  }

  async function submit() {
    if (!offer || !pairingId || !pinValid || submitting) return;
    setSubmitting(true);
    setMessage("");
    try {
      const result = await leaseApi<PairingCommandStatus>(base, {
        pairingId,
        pin,
        idempotencyKey: crypto.randomUUID(),
      });
      setCommandId(result.commandId);
      setCommandStatus(result.status);
      setPin("");
      toast.success("ส่ง PIN ไปยังเครื่องแล้ว กำลังตรวจสอบสถานะ");
    } catch (error) {
      const text = leaseErrorMessage(error);
      setMessage(text);
      toast.error(text);
    } finally {
      setSubmitting(false);
    }
  }

  function restart() {
    setCommandId(null);
    setCommandStatus(null);
    setMessage("");
    setPin("");
    setOfferError("");
    setOfferLoading(true);
  }

  return (
    <Dialog
      open={open}
      onOpenChange={(next) => {
        if (!next) onClose();
      }}
      onOpenChangeComplete={(next) => {
        if (next) return;
        setOffer(null);
        setOfferLoading(true);
        setOfferError("");
        setPairingId("");
        setPin("");
        setSubmitting(false);
        setCommandId(null);
        setCommandStatus(null);
        setMessage("");
        setCopyState("idle");
        setCopyError("");
      }}
    >
      <DialogContent
        data-lenis-prevent
        initialFocus={copyButtonRef}
        finalFocus={returnFocusRef}
        className="max-h-[calc(100dvh-2rem)] overflow-y-auto"
      >
        <DialogHeader className="pr-8">
          <DialogTitle>เชื่อมต่อด้วย Moonlight</DialogTitle>
          <DialogDescription>
            คัดลอกที่อยู่ด้านบนไปเพิ่มใน Moonlight แล้วกรอก PIN 4 หลักที่
            Moonlight แสดง
          </DialogDescription>
        </DialogHeader>

        <div className="flex flex-col gap-2 rounded-2xl border bg-muted/40 p-3">
          <div className="flex items-start justify-between gap-3">
            <div className="min-w-0">
              <p className="text-xs text-muted-foreground">ที่อยู่เชื่อมต่อ</p>
              <p
                className="mt-0.5 truncate font-mono text-sm font-medium"
                title={address ?? undefined}
              >
                {address ?? "ยังไม่มีที่อยู่เชื่อมต่อสาธารณะ"}
              </p>
            </div>
            <Button
              ref={copyButtonRef}
              type="button"
              variant="outline"
              size="sm"
              onClick={() => void copyAddress()}
              disabled={!address}
            >
              {copyState === "copied" ? (
                <Check data-icon="inline-start" />
              ) : (
                <Copy data-icon="inline-start" />
              )}
              {copyState === "copied" ? "คัดลอกแล้ว" : "คัดลอก"}
            </Button>
          </div>
          {stale ? (
            <p className="text-xs text-status-busy">
              ข้อมูลที่อยู่เชื่อมต่ออาจล้าสมัย — ตรวจสอบกับผู้ดูแลก่อนใช้งาน
            </p>
          ) : null}
          {!address ? (
            <p className="text-xs text-muted-foreground">
              หากเพิ่มเครื่องนี้ไว้ใน Moonlight แล้ว สามารถกรอก PIN ด้านล่างได้เลย
            </p>
          ) : null}
          {copyError ? (
            <p role="status" className="text-xs text-destructive">
              {copyError}
            </p>
          ) : null}
        </div>

        {commandStarted ? (
          <div className="flex flex-col gap-3">
            {commandStatus === "succeeded" ? (
              <Alert>
                <Check aria-hidden />
                <AlertTitle>จับคู่สำเร็จ</AlertTitle>
                <AlertDescription>
                  กลับไปที่ Moonlight แล้วเชื่อมต่อเครื่องได้เลย
                </AlertDescription>
              </Alert>
            ) : commandStatus === "failed" ? (
              <Alert variant="destructive">
                <AlertTitle>จับคู่ไม่สำเร็จ</AlertTitle>
                <AlertDescription>
                  PIN ไม่ถูกต้องหรือหมดอายุ — เริ่มจับคู่ใหม่ใน Moonlight แล้วลองอีกครั้ง
                </AlertDescription>
              </Alert>
            ) : commandStatus === "expired" ? (
              <Alert>
                <AlertTitle>คำขอจับคู่หมดอายุแล้ว</AlertTitle>
                <AlertDescription>
                  เริ่มจับคู่ใหม่ใน Moonlight แล้วกด “เริ่มใหม่” เพื่อรับ PIN อีกครั้ง
                </AlertDescription>
              </Alert>
            ) : (
              <p
                role="status"
                className="flex items-center gap-2 text-sm text-muted-foreground"
              >
                <Spinner />
                กำลังส่ง PIN ไปยังเครื่องและรอผลลัพธ์…
              </p>
            )}
            {message ? (
              <p role="status" className="text-sm text-destructive">
                {message}
              </p>
            ) : null}
          </div>
        ) : offer ? (
          <div className="flex flex-col gap-4">
            <FieldGroup className="gap-4">
              {offer.pairingIds.length > 1 ? (
                <Field>
                  <FieldLabel htmlFor="moonlight-slot">อุปกรณ์ที่จับคู่</FieldLabel>
                  <Select
                    value={pairingId}
                    onValueChange={(value) => setPairingId(String(value ?? ""))}
                  >
                    <SelectTrigger id="moonlight-slot" className="w-full">
                      <SelectValue placeholder="เลือกอุปกรณ์" />
                    </SelectTrigger>
                    <SelectContent>
                      <SelectGroup>
                        {offer.pairingIds.map((id) => (
                          <SelectItem key={id} value={id}>
                            {pairingSlotLabel(id)}
                          </SelectItem>
                        ))}
                      </SelectGroup>
                    </SelectContent>
                  </Select>
                  <FieldDescription>
                    ใช้ได้ถึง {formatRentalDateTime(offer.validUntilUtc)}
                  </FieldDescription>
                </Field>
              ) : (
                <p className="text-xs text-muted-foreground">
                  พบคำขอจับคู่แล้ว · ใช้ได้ถึง{" "}
                  {formatRentalDateTime(offer.validUntilUtc)}
                </p>
              )}
              <Field data-invalid={pinInvalid}>
                <FieldLabel htmlFor="moonlight-pin">PIN 4 หลัก</FieldLabel>
                <Input
                  id="moonlight-pin"
                  inputMode="numeric"
                  pattern="[0-9]{4}"
                  maxLength={4}
                  autoComplete="off"
                  value={pin}
                  onChange={(event) =>
                    setPin(event.target.value.replace(/\D/g, "").slice(0, 4))
                  }
                  aria-invalid={pinInvalid}
                  aria-describedby={
                    pinInvalid ? "moonlight-pin-error" : undefined
                  }
                  className="text-center text-2xl tracking-[0.4em] tabular-nums"
                />
                {pinInvalid ? (
                  <FieldError id="moonlight-pin-error">
                    กรอก PIN เป็นตัวเลข 4 หลัก
                  </FieldError>
                ) : (
                  <FieldDescription>
                    PIN คือตัวเลข 4 หลักที่แสดงใน Moonlight
                  </FieldDescription>
                )}
              </Field>
            </FieldGroup>

            <Button
              onClick={() => void submit()}
              disabled={!canSubmit}
              className="w-full"
            >
              {submitting ? (
                <Spinner data-icon="inline-start" />
              ) : (
                <KeyRound data-icon="inline-start" />
              )}
              {submitting ? "กำลังส่ง PIN…" : "ยืนยัน PIN"}
            </Button>
            {message ? (
              <p role="status" className="text-sm text-destructive">
                {message}
              </p>
            ) : null}
          </div>
        ) : offerError ? (
          <Alert variant="destructive">
            <AlertTitle>โหลดช่องจับคู่ไม่สำเร็จ</AlertTitle>
            <AlertDescription>{offerError}</AlertDescription>
          </Alert>
        ) : offerLoading ? (
          <p
            role="status"
            className="flex items-center gap-2 text-sm text-muted-foreground"
          >
            <Spinner />
            กำลังตรวจหาคำขอจับคู่จาก Moonlight…
          </p>
        ) : (
          <div className="flex flex-col gap-2 rounded-2xl border border-dashed p-4">
            <p className="flex items-center gap-2 text-sm font-medium">
              <Spinner />
              กำลังรอคำขอจับคู่จาก Moonlight
            </p>
            <p className="text-sm text-muted-foreground">
              เปิด Moonlight แล้วเพิ่มเครื่องด้วยที่อยู่ด้านบน ระบบจะตรวจพบคำขอจับคู่ให้อัตโนมัติ
            </p>
          </div>
        )}

        <DialogFooter>
          {commandFinished ? (
            <Button variant="outline" onClick={restart}>
              <RefreshCw data-icon="inline-start" />
              เริ่มใหม่
            </Button>
          ) : null}
          <Button variant="outline" onClick={onClose}>
            ปิด
          </Button>
        </DialogFooter>
      </DialogContent>
    </Dialog>
  );
}
