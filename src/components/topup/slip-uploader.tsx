"use client";

import {
  useRef,
  useState,
  type ChangeEvent,
  type DragEvent,
  type FormEvent,
} from "react";
import Image from "next/image";
import { CircleAlert, Upload } from "lucide-react";
import { Alert, AlertDescription, AlertTitle } from "@/components/ui/alert";
import { Button } from "@/components/ui/button";
import { Spinner } from "@/components/ui/spinner";
import { cn } from "@/lib/utils";
import { MAX_SLIP_BYTES } from "@/lib/topup/constants";
import type { TopupDetail, TopupSlipResponse } from "@/lib/topup/types";
import { clientErrorMessage, topupFetch } from "@/components/topup/client";

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
  const [dragActive, setDragActive] = useState(false);
  const inputRef = useRef<HTMLInputElement>(null);

  if (topup.status !== "pending") return null;

  function applyFile(selected: File | null) {
    setError("");
    if (!selected) {
      setFile(null);
      return;
    }
    if (!ALLOWED_TYPES.has(selected.type)) {
      setError("รองรับเฉพาะรูป JPEG, PNG หรือ WebP");
      return;
    }
    if (selected.size > MAX_SLIP_BYTES) {
      setError("รูปสลิปต้องมีขนาดไม่เกิน 4 MB");
      return;
    }
    setFile(selected);
  }

  function chooseFile(event: ChangeEvent<HTMLInputElement>) {
    const selected = event.currentTarget.files?.[0] ?? null;
    applyFile(selected);
    if (!selected || !ALLOWED_TYPES.has(selected.type) || selected.size > MAX_SLIP_BYTES) {
      event.currentTarget.value = "";
    }
  }

  function dropFile(event: DragEvent<HTMLLabelElement>) {
    event.preventDefault();
    setDragActive(false);
    if (pending || disabled) return;
    applyFile(event.dataTransfer.files?.[0] ?? null);
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
      <label
        htmlFor={`slip-${topup.id}`}
        onDragOver={(event) => {
          event.preventDefault();
          if (!pending && !disabled) setDragActive(true);
        }}
        onDragLeave={() => setDragActive(false)}
        onDrop={dropFile}
        className={cn(
          "flex cursor-pointer flex-col items-center justify-center gap-2 rounded-2xl border border-dashed p-6 text-center transition-colors",
          dragActive ? "border-primary bg-primary/5" : "border-border hover:bg-muted/40",
          (pending || disabled) && "pointer-events-none opacity-60",
        )}
      >
        <input
          ref={inputRef}
          id={`slip-${topup.id}`}
          type="file"
          accept="image/jpeg,image/png,image/webp"
          className="sr-only"
          disabled={pending || disabled}
          onChange={chooseFile}
        />
        <Image
          src="/topup/slip.webp"
          alt=""
          width={48}
          height={48}
          className="size-12 rounded-lg object-contain opacity-80"
        />
        <span className="text-sm font-medium">
          {file ? file.name : "อัปโหลดสลิปใบเสร็จ หรือวางไฟล์ที่นี่"}
        </span>
        <span className="text-xs text-muted-foreground">
          รองรับไฟล์รูปภาพ jpg, png, webp · ไม่เกิน 4 MB
        </span>
      </label>

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
        {pending ? "กำลังตรวจสอบสลิป…" : "ยืนยันสลิป"}
      </Button>
      {pending ? (
        <p className="text-center text-xs text-muted-foreground" role="status" aria-live="polite">
          กำลังส่งให้ผู้ให้บริการตรวจสอบ กรุณาอย่าปิดหน้านี้
        </p>
      ) : null}
    </form>
  );
}
