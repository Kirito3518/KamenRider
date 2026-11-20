"use client";

import { useState, type FormEvent } from "react";
import { CircleAlert, QrCode } from "lucide-react";
import { Alert, AlertDescription, AlertTitle } from "@/components/ui/alert";
import { Button } from "@/components/ui/button";
import {
  Field,
  FieldDescription,
  FieldError,
  FieldGroup,
  FieldLabel,
} from "@/components/ui/field";
import { Input } from "@/components/ui/input";
import { Spinner } from "@/components/ui/spinner";
import { formatBahtNumber, parseBahtToSatang } from "@/lib/topup/amount";
import type {
  TopupCreateResponse,
  TopupDetail,
  TopupPublicConfig,
  TopupSlipResponse,
} from "@/lib/topup/types";
import { QrPanel } from "@/components/topup/qr-panel";
import {
  clientErrorMessage,
  newRequestId,
  topupFetch,
} from "@/components/topup/client";

const PRESETS = [100, 300, 500, 1_000];

export function PromptpayForm({
  config,
  onBalance,
  embedded = false,
}: {
  config: TopupPublicConfig;
  onBalance: (balanceSatang: number) => void;
  embedded?: boolean;
}) {
  const [amount, setAmount] = useState("100");
  const [active, setActive] = useState<TopupDetail | null>(null);
  const [error, setError] = useState("");
  const [fieldError, setFieldError] = useState("");
  const [pending, setPending] = useState(false);

  const unavailable = !config.promptpay.enabled;
  const verificationUnavailable = !config.slipVerificationAvailable;

  async function submit(event: FormEvent<HTMLFormElement>) {
    event.preventDefault();
    if (pending) return;
    const satang = parseBahtToSatang(amount);
    if (satang === null) {
      setFieldError("กรอกจำนวนเงินให้ถูกต้อง เช่น 100 หรือ 100.50");
      return;
    }
    if (satang < config.minSatang || satang > config.maxSatang) {
      setFieldError(
        `จำนวนเงินต้องอยู่ระหว่าง ${formatBahtNumber(config.minSatang)} – ${formatBahtNumber(config.maxSatang)} บาท`,
      );
      return;
    }
    setPending(true);
    setError("");
    setFieldError("");
    try {
      const result = await topupFetch<TopupCreateResponse>(
        "/api/topups/promptpay",
        {
          method: "POST",
          headers: { "Content-Type": "application/json" },
          body: JSON.stringify({
            requestId: newRequestId(),
            amountSatang: satang,
          }),
        },
      );
      setActive(result.topup);
      onBalance(result.balanceSatang);
      if (result.outcome === "failed") {
        setError(result.message || "สร้าง QR PromptPay ไม่สำเร็จ");
      }
    } catch (cause) {
      setError(clientErrorMessage(cause));
    } finally {
      setPending(false);
    }
  }

  function updated(result: TopupSlipResponse) {
    setActive(result.topup);
    onBalance(result.balanceSatang);
    if (result.outcome === "failed") setError(result.message);
    else setError("");
  }

  function reset() {
    setActive(null);
    setError("");
    setFieldError("");
  }

  if (active) {
    return (
      <QrPanel
        topup={active}
        onUpdated={updated}
        onReset={reset}
        inDialog={embedded}
      />
    );
  }

  return (
    <div className="flex flex-col gap-4">
      {!embedded ? (
        <div className="flex items-start gap-3 rounded-2xl border bg-card p-4">
          <QrCode aria-hidden className="mt-0.5 size-5 text-primary" />
          <div>
            <p className="font-medium">สร้าง QR PromptPay ตามจำนวนที่ต้องการ</p>
            <p className="text-sm text-muted-foreground">
              สแกนจ่ายด้วยแอปธนาคาร แล้วกลับมาอัปโหลดสลิปเพื่อให้ระบบตรวจสอบอัตโนมัติ
            </p>
          </div>
        </div>
      ) : null}

      {unavailable ? (
        <Alert>
          <CircleAlert aria-hidden />
          <AlertTitle>ยังไม่เปิดให้บริการ</AlertTitle>
          <AlertDescription>
            ช่องทาง PromptPay QR ยังไม่เปิดใช้งาน กรุณาติดต่อผู้ดูแลระบบ
          </AlertDescription>
        </Alert>
      ) : null}
      {verificationUnavailable ? (
        <Alert variant="destructive">
          <CircleAlert aria-hidden />
          <AlertTitle>ระบบตรวจสลิปยังไม่พร้อม</AlertTitle>
          <AlertDescription>
            ผู้ดูแลระบบยังไม่ได้ตั้งค่าผู้ให้บริการตรวจสลิป กรุณาติดต่อผู้ดูแล
          </AlertDescription>
        </Alert>
      ) : null}

      <form onSubmit={(event) => void submit(event)} noValidate>
        <FieldGroup className="max-w-xl gap-4">
          <Field data-invalid={Boolean(fieldError)}>
            <FieldLabel htmlFor="promptpay-amount">จำนวนเงินที่ต้องการเติม (บาท)</FieldLabel>
            <Input
              id="promptpay-amount"
              name="amount"
              value={amount}
              inputMode="decimal"
              autoComplete="off"
              maxLength={12}
              aria-invalid={Boolean(fieldError)}
              aria-describedby={fieldError ? "promptpay-amount-error" : "promptpay-amount-hint"}
              onChange={(event) => {
                setAmount(event.currentTarget.value);
                setFieldError("");
              }}
              disabled={pending || unavailable || verificationUnavailable}
            />
            <FieldDescription id="promptpay-amount-hint">
              ขั้นต่ำ {formatBahtNumber(config.minSatang)} บาท · สูงสุด{" "}
              {formatBahtNumber(config.maxSatang)} บาท
            </FieldDescription>
            {fieldError ? (
              <FieldError id="promptpay-amount-error">{fieldError}</FieldError>
            ) : null}
          </Field>
          <div className="flex flex-wrap gap-2">
            {PRESETS.map((preset) => {
              const value = `${preset}.00`;
              const selected = amount === value || amount === String(preset);
              return (
                <Button
                  key={preset}
                  type="button"
                  size="sm"
                  variant={selected ? "default" : "outline"}
                  onClick={() => {
                    setAmount(value);
                    setFieldError("");
                  }}
                  disabled={pending || unavailable}
                >
                  {preset.toLocaleString("th-TH")} บาท
                </Button>
              );
            })}
          </div>
          <Button
            type="submit"
            disabled={pending || unavailable || verificationUnavailable}
          >
            {pending ? (
              <Spinner data-icon="inline-start" />
            ) : (
              <QrCode data-icon="inline-start" />
            )}
            {pending ? "กำลังสร้าง QR…" : "สร้าง QR PromptPay"}
          </Button>
        </FieldGroup>
      </form>

      {error ? (
        <Alert variant="destructive" role="alert">
          <CircleAlert aria-hidden />
          <AlertTitle>สร้างรายการไม่สำเร็จ</AlertTitle>
          <AlertDescription>{error}</AlertDescription>
        </Alert>
      ) : null}
      {!embedded && config.promptpay.accountName ? (
        <p className="text-xs text-muted-foreground">
          ผู้รับเงิน: {config.promptpay.accountName}
        </p>
      ) : null}
    </div>
  );
}
