"use client";

import { useRef, useState, type RefObject } from "react";
import Link from "next/link";
import { useRouter } from "next/navigation";
import { AlertTriangle, Info, Minus, Plus, Wallet } from "lucide-react";
import toast from "react-hot-toast";
import { Button } from "@/components/ui/button";
import {
  Dialog,
  DialogContent,
  DialogDescription,
  DialogHeader,
  DialogTitle,
} from "@/components/ui/dialog";
import {
  Field,
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
import { Alert, AlertDescription, AlertTitle } from "@/components/ui/alert";
import { Spinner } from "@/components/ui/spinner";
import { authClient } from "@/lib/auth-client";
import { formatBalance } from "@/lib/money";
import { formatRamBytes, formatSpec } from "@/lib/machine-specs";
import { type RentalMachine } from "@/lib/rental-machines";

const MAX_HOURS = 24;

export function RentalDialog({
  machine,
  onClose,
  returnFocusRef,
}: {
  machine: RentalMachine | null;
  onClose: () => void;
  returnFocusRef: RefObject<HTMLButtonElement | null>;
}) {
  const router = useRouter();
  const { data: session, isPending: sessionPending, refetch } = authClient.useSession();
  const [hours, setHours] = useState("1");
  const [requestId, setRequestId] = useState<string | null>(null);
  const [pending, setPending] = useState(false);
  const [error, setError] = useState("");
  const durationRef = useRef<HTMLInputElement>(null);

  const numericHours = Number(hours);
  const validHours =
    Number.isSafeInteger(numericHours) &&
    numericHours >= 1 &&
    numericHours <= MAX_HOURS;
  const priceSatang = machine?.priceSatang ?? 0;
  const totalSatang = validHours ? priceSatang * numericHours : 0;
  const balanceSatang = session?.user.balanceSatang ?? 0;
  const insufficient = Boolean(session) && validHours && totalSatang > balanceSatang;
  const showError = hours !== "" && !validHours;

  function updateHours(value: string) {
    setHours(value);
    // A new duration is a new request; reusing the previous id would conflict.
    setRequestId(null);
    setError("");
  }

  async function submit() {
    if (!machine || !session || !validHours || insufficient || pending) return;
    const id = requestId ?? crypto.randomUUID();
    setRequestId(id);
    setPending(true);
    setError("");
    try {
      const response = await fetch("/api/rentals", {
        method: "POST",
        headers: { "Content-Type": "application/json" },
        cache: "no-store",
        body: JSON.stringify({
          machineId: machine.id,
          hours: numericHours,
          requestId: id,
        }),
      });
      const payload = (await response.json().catch(() => ({}))) as { error?: string };
      if (!response.ok) {
        setError(payload.error || "เริ่มการเช่าไม่สำเร็จ กรุณาลองใหม่");
        return;
      }
      setRequestId(null);
      await refetch();
      toast.success("ส่งคำขอเช่าแล้ว กำลังเตรียมเครื่องให้คุณ");
      onClose();
      router.push("/my-machines");
    } catch {
      setError("เชื่อมต่อระบบไม่ได้ กรุณาลองใหม่");
    } finally {
      setPending(false);
    }
  }

  return (
    <Dialog
      open={!!machine}
      onOpenChange={(open) => {
        if (!open) onClose();
      }}
      onOpenChangeComplete={(open) => {
        if (!open) {
          setHours("1");
          setRequestId(null);
          setError("");
          setPending(false);
        }
      }}
    >
      <DialogContent
        data-lenis-prevent
        className="max-h-[calc(100dvh-2rem)] overflow-y-auto rounded-2xl"
        initialFocus={durationRef}
        finalFocus={returnFocusRef}
      >
        <DialogHeader className="pr-8">
          <DialogTitle>เช่าเครื่อง {machine?.name || machine?.id}</DialogTitle>
          <DialogDescription>
            ชำระค่าเช่าจากยอดเงินคงเหลือของคุณ แล้วระบบจะเตรียมเครื่องให้ทันที
          </DialogDescription>
        </DialogHeader>
        {machine && (
          <>
            <dl className="grid grid-cols-[auto_1fr] gap-x-6 gap-y-3 text-sm">
              <dt className="text-muted-foreground">CPU</dt>
              <dd>{formatSpec(machine.cpu)}</dd>
              <dt className="text-muted-foreground">การ์ดจอ</dt>
              <dd>{formatSpec(machine.gpu)}</dd>
              <dt className="text-muted-foreground">แรม</dt>
              <dd>
                {machine.ramBytes > 0
                  ? formatRamBytes(machine.ramBytes)
                  : machine.ram > 0
                    ? `${machine.ram} GB`
                    : formatRamBytes(undefined)}
              </dd>
              <dt className="text-muted-foreground">ราคา</dt>
              <dd>{formatBalance(machine.priceSatang)} / ชั่วโมง</dd>
            </dl>

            {sessionPending ? (
              <p role="status" className="flex items-center gap-2 text-sm text-muted-foreground">
                <Spinner data-icon="inline-start" />
                กำลังตรวจสอบบัญชี…
              </p>
            ) : !session ? (
              <div className="flex flex-col gap-3 rounded-2xl border p-4">
                <p className="text-sm">กรุณาเข้าสู่ระบบก่อนทำการเช่า</p>
                <Button nativeButton={false} render={<Link href="/login" />} className="w-full">
                  เข้าสู่ระบบ / สมัครสมาชิก
                </Button>
              </div>
            ) : (
              <>
                <div className="flex items-center justify-between gap-4 rounded-2xl bg-muted/50 px-4 py-3 text-sm">
                  <span className="flex items-center gap-2 text-muted-foreground">
                    <Wallet aria-hidden className="size-4" />
                    ยอดเงินคงเหลือ
                  </span>
                  <span className="font-medium tabular-nums">
                    {formatBalance(balanceSatang)}
                  </span>
                </div>
                <FieldGroup>
                  <Field data-invalid={showError}>
                    <FieldLabel htmlFor="rental-duration">
                      ระยะเวลาเช่า (ชั่วโมง)
                    </FieldLabel>
                    <InputGroup>
                      <InputGroupAddon>
                        <InputGroupButton
                          size="icon-sm"
                          variant="outline"
                          aria-label="ลดเวลาเช่า 1 ชั่วโมง"
                          disabled={!validHours || numericHours <= 1 || pending}
                          onClick={() => updateHours(String(numericHours - 1))}
                        >
                          <Minus />
                        </InputGroupButton>
                      </InputGroupAddon>
                      <InputGroupInput
                        ref={durationRef}
                        id="rental-duration"
                        type="number"
                        inputMode="numeric"
                        min={1}
                        max={MAX_HOURS}
                        step={1}
                        required
                        value={hours}
                        disabled={pending}
                        onChange={(event) => updateHours(event.target.value)}
                        onBlur={() => {
                          if (hours === "") updateHours("1");
                        }}
                        aria-invalid={showError}
                        aria-describedby={
                          showError ? "rental-duration-error" : undefined
                        }
                        className="text-center tabular-nums [appearance:textfield] [&::-webkit-inner-spin-button]:appearance-none [&::-webkit-outer-spin-button]:appearance-none"
                      />
                      <InputGroupAddon align="inline-end">
                        <InputGroupButton
                          size="icon-sm"
                          variant="default"
                          aria-label="เพิ่มเวลาเช่า 1 ชั่วโมง"
                          disabled={!validHours || numericHours >= MAX_HOURS || pending}
                          onClick={() =>
                            updateHours(String(validHours ? Math.min(numericHours + 1, MAX_HOURS) : 1))
                          }
                        >
                          <Plus />
                        </InputGroupButton>
                      </InputGroupAddon>
                    </InputGroup>
                    {showError ? (
                      <FieldError id="rental-duration-error">
                        กรอกจำนวนชั่วโมงเต็ม 1–{MAX_HOURS} ชั่วโมง
                      </FieldError>
                    ) : (
                      <p className="text-sm text-muted-foreground">
                        คิดค่าเช่าเป็นชั่วโมงเต็ม เริ่มต้น 1 ชั่วโมง สูงสุด {MAX_HOURS} ชั่วโมง
                      </p>
                    )}
                  </Field>
                </FieldGroup>
                <div className="flex items-center justify-between gap-4">
                  <span className="text-sm text-muted-foreground">
                    ยอดรวม{validHours ? ` ${numericHours} ชั่วโมง` : ""}
                  </span>
                  <p className="text-3xl font-bold text-primary" aria-live="polite">
                    {validHours ? formatBalance(totalSatang) : "—"}
                  </p>
                </div>
                {insufficient ? (
                  <Alert variant="destructive">
                    <AlertTriangle aria-hidden />
                    <AlertTitle>ยอดเงินไม่เพียงพอ</AlertTitle>
                    <AlertDescription>
                      ต้องการ {formatBalance(totalSatang)} แต่คงเหลือ{" "}
                      {formatBalance(balanceSatang)} กรุณาติดต่อผู้ดูแลเพื่อเติมเงิน
                    </AlertDescription>
                  </Alert>
                ) : null}
                {error ? (
                  <Alert variant="destructive" role="alert">
                    <AlertTriangle aria-hidden />
                    <AlertTitle>เริ่มการเช่าไม่สำเร็จ</AlertTitle>
                    <AlertDescription>{error}</AlertDescription>
                  </Alert>
                ) : null}
                <Button
                  disabled={pending || !validHours || insufficient}
                  onClick={submit}
                  className="w-full"
                >
                  {pending ? <Spinner data-icon="inline-start" /> : null}
                  {pending ? "กำลังดำเนินการ…" : "ยืนยันการเช่า"}
                </Button>
                <p className="flex items-start gap-2 text-xs text-muted-foreground">
                  <Info aria-hidden className="mt-0.5 size-3.5 shrink-0" />
                  เมื่อสิ้นสุดการเช่าก่อนกำหนด ระบบจะคืนเงินตามเวลาที่ไม่ได้ใช้ให้อัตโนมัติ
                </p>
              </>
            )}
          </>
        )}
      </DialogContent>
    </Dialog>
  );
}
