"use client";

import { useState, type FormEvent } from "react";
import Link from "next/link";
import {
  CircleAlert,
  CircleCheck,
  Gift,
  History,
  RotateCcw,
  ShieldCheck,
} from "lucide-react";
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
import { formatBalance } from "@/lib/money";
import type {
  TopupCreateResponse,
  TopupDetail,
  TopupPublicConfig,
} from "@/lib/topup/types";
import {
  clientErrorMessage,
  newRequestId,
  topupFetch,
} from "@/components/topup/client";

export function TrueMoneyForm({
  config,
  onBalance,
  embedded = false,
}: {
  config: TopupPublicConfig;
  onBalance: (balanceSatang: number) => void;
  embedded?: boolean;
}) {
  const [link, setLink] = useState("");
  const [pending, setPending] = useState(false);
  const [error, setError] = useState("");
  const [fieldError, setFieldError] = useState("");
  const [active, setActive] = useState<TopupDetail | null>(null);
  const [message, setMessage] = useState("");
  const [retrying, setRetrying] = useState(false);

  async function submit(event: FormEvent<HTMLFormElement>) {
    event.preventDefault();
    if (pending) return;
    const value = link.trim();
    if (!value) {
      setFieldError("กรอกลิงก์ซองอั่งเปาหรือรหัสซอง");
      return;
    }
    setPending(true);
    setError("");
    setFieldError("");
    setMessage("");
    try {
      const result = await topupFetch<TopupCreateResponse>("/api/topups/truemoney", {
        method: "POST",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify({ requestId: newRequestId(), link: value }),
      });
      setActive(result.topup);
      onBalance(result.balanceSatang);
      setMessage(result.message ?? "");
      if (result.outcome === "failed") {
        setError(result.message || "รับซองอั่งเปาไม่สำเร็จ");
      }
    } catch (cause) {
      setError(clientErrorMessage(cause));
    } finally {
      setPending(false);
    }
  }

  async function retry() {
    if (!active || retrying) return;
    setRetrying(true);
    setError("");
    try {
      const result = await topupFetch<TopupCreateResponse>(
        `/api/topups/${active.id}/retry`,
        { method: "POST" },
      );
      setActive(result.topup);
      onBalance(result.balanceSatang);
      setMessage(result.message ?? "");
      if (result.outcome === "failed") {
        setError(result.message || "รับซองอั่งเปาไม่สำเร็จ");
      }
    } catch (cause) {
      setError(clientErrorMessage(cause));
    } finally {
      setRetrying(false);
    }
  }

  function reset() {
    setActive(null);
    setError("");
    setMessage("");
    setLink("");
  }

  if (active) {
    if (active.status === "completed") {
      return (
        <div className="flex flex-col gap-3">
          <Alert>
            <CircleCheck aria-hidden />
            <AlertTitle>รับซองอั่งเปาสำเร็จ</AlertTitle>
            <AlertDescription>
              ได้รับเงิน {formatBalance(active.creditedSatang)} เข้ากระเป๋าแล้ว
              {message ? ` · ${message}` : ""}
            </AlertDescription>
          </Alert>
          <div className="flex flex-wrap gap-2">
            <Button nativeButton={false} render={<Link href="/history" />}>
              <History data-icon="inline-start" />
              ดูประวัติการเติมเงิน
            </Button>
            <Button variant="outline" onClick={reset}>
              <RotateCcw data-icon="inline-start" />
              รับซองอีกครั้ง
            </Button>
          </div>
        </div>
      );
    }
    if (active.status === "failed") {
      return (
        <div className="flex flex-col gap-3">
          <Alert variant="destructive">
            <CircleAlert aria-hidden />
            <AlertTitle>รับซองอั่งเปาไม่สำเร็จ</AlertTitle>
            <AlertDescription>
              {error || active.failureMessage || "ไม่สามารถรับเงินจากซองนี้ได้"}
            </AlertDescription>
          </Alert>
          <div className="flex flex-wrap gap-2">
            <Button onClick={reset}>
              <RotateCcw data-icon="inline-start" />
              ลองซองอื่น
            </Button>
            <Button variant="outline" nativeButton={false} render={<Link href="/history" />}>
              <History data-icon="inline-start" />
              ดูประวัติ
            </Button>
          </div>
        </div>
      );
    }
    return (
      <div className="flex flex-col gap-3">
        <Alert variant="destructive">
          <CircleAlert aria-hidden />
          <AlertTitle>
            {active.status === "pending"
              ? "ระบบถูกเรียกถี่เกินไป"
              : "ไม่ทราบผลการรับเงิน"}
          </AlertTitle>
          <AlertDescription>
            {active.failureCode === "RATE_LIMITED"
              ? "กรุณากดลองอีกครั้งภายหลัง"
              : "ระบบจะไม่เพิ่มยอดเงินจนกว่าจะยืนยันผลได้ กรุณาติดต่อผู้ดูแลระบบหากเงินถูกหักจากซองแล้ว"}
          </AlertDescription>
        </Alert>
        {error ? (
          <Alert variant="destructive">
            <CircleAlert aria-hidden />
            <AlertDescription>{error}</AlertDescription>
          </Alert>
        ) : null}
        <div className="flex flex-wrap gap-2">
          {active.canRetry ? (
            <Button onClick={() => void retry()} disabled={retrying}>
              {retrying ? (
                <Spinner data-icon="inline-start" />
              ) : (
                <RotateCcw data-icon="inline-start" />
              )}
              ลองอีกครั้ง
            </Button>
          ) : null}
          <Button variant="outline" nativeButton={false} render={<Link href="/history" />}>
            <History data-icon="inline-start" />
            ตรวจสอบประวัติ
          </Button>
          <Button variant="ghost" onClick={reset}>
            เริ่มใหม่
          </Button>
        </div>
      </div>
    );
  }

  return (
    <div className="flex flex-col gap-4">
      {!embedded ? (
        <div className="flex items-start gap-3 rounded-2xl border bg-card p-4">
          <Gift aria-hidden className="mt-0.5 size-5 text-primary" />
          <div>
            <p className="font-medium">รับเงินจากซองอั่งเปา TrueMoney</p>
            <p className="text-sm text-muted-foreground">
              วางลิงก์ซองอั่งเปาหรือรหัสซอง ระบบจะตรวจสอบและเพิ่มเงินตามยอดที่ได้รับจริง
            </p>
          </div>
        </div>
      ) : null}

      {!config.truemoney.enabled ? (
        <Alert>
          <CircleAlert aria-hidden />
          <AlertTitle>ยังไม่เปิดให้บริการ</AlertTitle>
          <AlertDescription>
            ช่องทาง TrueMoney Angpao ยังไม่เปิดใช้งาน กรุณาติดต่อผู้ดูแลระบบ
          </AlertDescription>
        </Alert>
      ) : null}

      <form onSubmit={(event) => void submit(event)} noValidate>
        <FieldGroup className="max-w-2xl gap-4">
          <Field data-invalid={Boolean(fieldError)}>
            <FieldLabel htmlFor="truemoney-link">ลิงก์ซองอั่งเปา TrueMoney</FieldLabel>
            <Input
              id="truemoney-link"
              name="link"
              value={link}
              placeholder="https://gift.truemoney.com/campaign/?v=..."
              autoComplete="off"
              maxLength={512}
              aria-invalid={Boolean(fieldError)}
              onChange={(event) => {
                setLink(event.currentTarget.value);
                setFieldError("");
              }}
              disabled={pending || !config.truemoney.enabled}
            />
            {fieldError ? (
              <FieldError>{fieldError}</FieldError>
            ) : (
              <FieldDescription>
                รองรับลิงก์จาก gift.truemoney.com หรือรหัสซองอั่งเปา · 1 ลิงก์ใช้ได้ครั้งเดียว
              </FieldDescription>
            )}
          </Field>
          <Button type="submit" disabled={pending || !config.truemoney.enabled}>
            {pending ? (
              <Spinner data-icon="inline-start" />
            ) : (
              <Gift data-icon="inline-start" />
            )}
            {pending ? "กำลังรับเงิน…" : "ยืนยันรับเงิน"}
          </Button>
        </FieldGroup>
      </form>

      {!embedded ? (
        <div className="flex items-start gap-3 rounded-2xl border border-dashed p-4 text-sm text-muted-foreground">
          <ShieldCheck aria-hidden className="mt-0.5 size-4 shrink-0" />
          <p>
            ระบบไม่เชื่อยอดเงินจากเบราว์เซอร์ แต่จะใช้ยอดที่ผู้ให้บริการยืนยันแล้วเท่านั้น
            และจะเพิ่มยอดเงินเพียงครั้งเดียว
          </p>
        </div>
      ) : null}

      {error ? (
        <Alert variant="destructive" role="alert">
          <CircleAlert aria-hidden />
          <AlertTitle>รับซองอั่งเปาไม่สำเร็จ</AlertTitle>
          <AlertDescription>{error}</AlertDescription>
        </Alert>
      ) : null}
    </div>
  );
}
