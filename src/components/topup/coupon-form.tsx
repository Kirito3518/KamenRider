"use client";

import { useState, type FormEvent } from "react";
import { CircleAlert, TicketPercent } from "lucide-react";
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
import type { TopupPublicConfig } from "@/lib/topup/types";

export function CouponForm({
  config,
  embedded = false,
}: {
  config: TopupPublicConfig;
  embedded?: boolean;
}) {
  const [code, setCode] = useState("");
  const [message, setMessage] = useState("");
  const [fieldError, setFieldError] = useState("");

  function submit(event: FormEvent<HTMLFormElement>) {
    event.preventDefault();
    if (!code.trim()) {
      setFieldError("กรอกรหัสคูปอง");
      return;
    }
    setFieldError("");
    setMessage(
      config.coupon.enabled
        ? config.coupon.message || "ระบบคูปองยังไม่เปิดให้บริการในขณะนี้"
        : config.coupon.message || "คูปองยังไม่เปิดให้บริการในขณะนี้",
    );
  }

  return (
    <div className="flex flex-col gap-4">
      {!embedded ? (
        <div className="flex items-start gap-3 rounded-2xl border bg-card p-4">
          <TicketPercent aria-hidden className="mt-0.5 size-5 text-primary" />
          <div>
            <p className="font-medium">ใช้รหัสคูปอง</p>
            <p className="text-sm text-muted-foreground">
              ระบบคูปองอยู่ระหว่างการพัฒนา UI นี้พร้อมเชื่อมต่อระบบจริงในอนาคต
            </p>
          </div>
        </div>
      ) : null}

      <Alert>
        <CircleAlert aria-hidden />
        <AlertTitle>
          {config.coupon.enabled ? "คูปองอยู่ระหว่างเปิดให้บริการ" : "ยังไม่เปิดให้บริการ"}
        </AlertTitle>
        <AlertDescription>
          {config.coupon.message || "คูปองยังไม่เปิดให้บริการในขณะนี้"}
        </AlertDescription>
      </Alert>

      <form onSubmit={submit} noValidate>
        <FieldGroup className="max-w-xl gap-4">
          <Field data-invalid={Boolean(fieldError)}>
            <FieldLabel htmlFor="coupon-code">รหัสคูปอง</FieldLabel>
            <Input
              id="coupon-code"
              name="code"
              value={code}
              placeholder="กรอกรหัสคูปอง"
              autoComplete="off"
              maxLength={64}
              aria-invalid={Boolean(fieldError)}
              onChange={(event) => {
                setCode(event.currentTarget.value);
                setFieldError("");
                setMessage("");
              }}
            />
            {fieldError ? <FieldError>{fieldError}</FieldError> : null}
            <FieldDescription>
              ยังไม่มีการเรียก API หรือตรวจสอบคูปองจริงในเวอร์ชันนี้
            </FieldDescription>
          </Field>
          <Button type="submit">
            <TicketPercent data-icon="inline-start" />
            ยืนยันรหัสคูปอง
          </Button>
        </FieldGroup>
      </form>

      {message ? (
        <Alert role="status">
          <CircleAlert aria-hidden />
          <AlertTitle>สถานะคูปอง</AlertTitle>
          <AlertDescription>{message}</AlertDescription>
        </Alert>
      ) : null}
    </div>
  );
}
