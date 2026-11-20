"use client";

import { useRef, useState, type ChangeEvent, type FormEvent } from "react";
import { CircleAlert, Upload } from "lucide-react";
import { Alert, AlertDescription, AlertTitle } from "@/components/ui/alert";
import { Button } from "@/components/ui/button";
import { Field, FieldDescription, FieldLabel } from "@/components/ui/field";
import { Input } from "@/components/ui/input";
import { Spinner } from "@/components/ui/spinner";
import { MAX_SLIP_BYTES } from "@/lib/topup/constants";
import type { TopupDetail, TopupSlipResponse } from "@/lib/topup/types";
import {
  clientErrorMessage,
  topupFetch,
} from "@/components/topup/client";

const ALLOWED_TYPES = new Set(["image/jpeg", "image/png", "image/webp"]);

export function SlipUploader({
  topup,
  onUpdated,
  disabled = false,
}: {
  topup: TopupDetail;
  onUpdated: (result: TopupSlipResponse) => void;
  disabled?: boolean;
}) {
  const [file, setFile] = useState<File | null>(null);
  const [error, setError] = useState("");
  const [pending, setPending] = useState(false);
  const inputRef = useRef<HTMLInputElement>(null);

  if (topup.status !== "pending") return null;

  function chooseFile(event: ChangeEvent<HTMLInputElement>) {
    const selected = event.currentTarget.files?.[0] ?? null;
    setError("");
    if (!selected) {
      setFile(null);
      return;
    }
    if (!ALLOWED_TYPES.has(selected.type)) {
      setError("รองรับเฉพาะรูป JPEG, PNG หรือ WebP");
      event.currentTarget.value = "";
      setFile(null);
      return;
    }
    if (selected.size > MAX_SLIP_BYTES) {
      setError("รูปสลิปต้องมีขนาดไม่เกิน 4 MB");
      event.currentTarget.value = "";
      setFile(null);
      return;
    }
    setFile(selected);
  }

  async function submit(event: FormEvent<HTMLFormElement>) {
    event.preventDefault();
    if (pending || disabled || !file) return;
    setPending(true);
    setError("");
    try {
      const formData = new FormData();
      formData.set("file", file);
      const result = await topupFetch<TopupSlipResponse>(
        `/api/topups/${topup.id}/slip`,
        { method: "POST", body: formData },
      );
      onUpdated(result);
      setFile(null);
      if (inputRef.current) inputRef.current.value = "";
    } catch (cause) {
      setError(clientErrorMessage(cause));
    } finally {
      setPending(false);
    }
  }

  return (
    <form onSubmit={(event) => void submit(event)} className="flex flex-col gap-3">
      <Field data-invalid={Boolean(error)}>
        <FieldLabel htmlFor={`slip-${topup.id}`}>อัปโหลดสลิปการโอน</FieldLabel>
        <Input
          ref={inputRef}
          id={`slip-${topup.id}`}
          type="file"
          accept="image/jpeg,image/png,image/webp"
          aria-invalid={Boolean(error)}
          aria-describedby={error ? `slip-error-${topup.id}` : undefined}
          disabled={pending || disabled}
          onChange={chooseFile}
        />
        <FieldDescription>
          รองรับ JPEG, PNG หรือ WebP ขนาดไม่เกิน 4 MB · ระบบตรวจสอบอัตโนมัติ
        </FieldDescription>
      </Field>
      {file ? (
        <p className="truncate text-xs text-muted-foreground">
          เลือกไฟล์: {file.name}
        </p>
      ) : null}
      {error ? (
        <Alert variant="destructive" role="alert">
          <CircleAlert aria-hidden />
          <AlertTitle>อัปโหลดสลิปไม่สำเร็จ</AlertTitle>
          <AlertDescription>{error}</AlertDescription>
        </Alert>
      ) : null}
      <Button type="submit" disabled={pending || disabled || !file}>
        {pending ? (
          <Spinner data-icon="inline-start" />
        ) : (
          <Upload data-icon="inline-start" />
        )}
        {pending ? "กำลังตรวจสอบสลิป…" : "ส่งสลิปเพื่อตรวจสอบ"}
      </Button>
      {pending ? (
        <p className="text-xs text-muted-foreground" role="status" aria-live="polite">
          กำลังส่งให้ผู้ให้บริการตรวจสอบ กรุณาอย่าปิดหน้านี้
        </p>
      ) : null}
    </form>
  );
}
