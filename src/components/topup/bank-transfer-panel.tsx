"use client";

import { useState } from "react";
import Link from "next/link";
import {
  Banknote,
  CircleAlert,
  CircleCheck,
  Clock3,
  Copy,
  History,
  RotateCcw,
} from "lucide-react";
import toast from "react-hot-toast";
import { Alert, AlertDescription, AlertTitle } from "@/components/ui/alert";
import { Badge } from "@/components/ui/badge";
import { Button } from "@/components/ui/button";
import { Spinner } from "@/components/ui/spinner";
import { formatBalance } from "@/lib/money";
import { formatBangkokDateTime } from "@/lib/date-range";
import {
  formatBankAccountNumber,
  maskAccountNumber,
} from "@/lib/topup/amount";
import type { TopupDetail, TopupSlipResponse } from "@/lib/topup/types";
import { BankLogo } from "@/components/topup/topup-display";
import { SlipUploader } from "@/components/topup/slip-uploader";
import {
  clientErrorMessage,
  copyText,
  topupFetch,
} from "@/components/topup/client";

function CopyButton({ value, label }: { value: string; label: string }) {
  return (
    <Button
      type="button"
      variant="ghost"
      size="icon-sm"
      aria-label={`คัดลอก${label}`}
      title={`คัดลอก${label}`}
      onClick={() => {
        void copyText(value).then((ok) => {
          if (ok) toast.success(`คัดลอก${label}แล้ว`);
          else toast.error("คัดลอกไม่สำเร็จ");
        });
      }}
    >
      <Copy />
    </Button>
  );
}

export function BankTransferPanel({
  topup,
  onUpdated,
  onReset,
}: {
  topup: TopupDetail;
  onUpdated: (result: TopupSlipResponse) => void;
  onReset: () => void;
}) {
  const [retrying, setRetrying] = useState(false);
  const [retryError, setRetryError] = useState("");

  async function retry() {
    if (retrying) return;
    setRetrying(true);
    setRetryError("");
    try {
      const result = await topupFetch<TopupSlipResponse>(
        `/api/topups/${topup.id}/retry`,
        { method: "POST" },
      );
      onUpdated(result);
    } catch (cause) {
      setRetryError(clientErrorMessage(cause));
    } finally {
      setRetrying(false);
    }
  }

  if (topup.status === "completed") {
    return (
      <div className="flex flex-col gap-3">
        <Alert>
          <CircleCheck aria-hidden />
          <AlertTitle>เติมเงินสำเร็จ</AlertTitle>
          <AlertDescription>
            ได้รับเงิน {formatBalance(topup.creditedSatang)} เข้ากระเป๋าแล้ว
          </AlertDescription>
        </Alert>
        <div className="flex flex-wrap gap-2">
          <Button nativeButton={false} render={<Link href="/history" />}>
            <History data-icon="inline-start" />
            ดูประวัติการเติมเงิน
          </Button>
          <Button variant="outline" onClick={onReset}>
            <RotateCcw data-icon="inline-start" />
            เติมเงินอีกครั้ง
          </Button>
        </div>
      </div>
    );
  }

  if (topup.status === "failed" || topup.status === "expired") {
    return (
      <div className="flex flex-col gap-3">
        <Alert variant="destructive">
          <CircleAlert aria-hidden />
          <AlertTitle>
            {topup.status === "expired" ? "รายการหมดอายุ" : "ตรวจสอบสลิปไม่สำเร็จ"}
          </AlertTitle>
          <AlertDescription>
            {topup.status === "expired"
              ? "ระบบไม่เพิ่มยอดเงินจากรายการที่หมดอายุ กรุณาสร้างรายการใหม่"
              : topup.failureMessage || topup.verificationMessage || "ข้อมูลไม่ตรงกับรายการ"}
          </AlertDescription>
        </Alert>
        <div className="flex flex-wrap gap-2">
          <Button onClick={onReset}>
            <RotateCcw data-icon="inline-start" />
            เริ่มรายการใหม่
          </Button>
          <Button variant="outline" nativeButton={false} render={<Link href="/history" />}>
            <History data-icon="inline-start" />
            ดูประวัติ
          </Button>
        </div>
      </div>
    );
  }

  const accountNumber = topup.receiverAccountNumber || "";

  return (
    <div className="flex flex-col gap-5">
      <div className="flex flex-wrap items-center justify-between gap-2">
        <div className="flex items-center gap-2">
          <Banknote aria-hidden className="size-5 text-primary" />
          <h3 className="text-lg font-semibold">โอนเงินเข้าบัญชีธนาคาร</h3>
        </div>
        <Badge variant={topup.status === "processing" ? "busy" : "default"}>
          {topup.status === "processing" ? "กำลังตรวจสอบ" : "รอชำระเงิน"}
        </Badge>
      </div>

      <div className="rounded-2xl border bg-card p-4">
        <div className="flex items-center gap-3 border-b pb-3">
          <BankLogo
            bankCode={topup.bankCode || ""}
            bankName={topup.bankName}
          />
          <div className="min-w-0">
            <p className="truncate font-semibold">
              {topup.bankName || "ธนาคาร"}
              {topup.bankCode ? (
                <span className="ml-2 font-mono text-xs text-muted-foreground">
                  {topup.bankCode}
                </span>
              ) : null}
            </p>
            <p className="truncate text-sm text-muted-foreground">
              {topup.receiverAccountName || "—"}
            </p>
          </div>
        </div>
        <dl className="grid gap-3 pt-3 text-sm sm:grid-cols-2">
          <div>
            <dt className="text-muted-foreground">เลขบัญชี</dt>
            <dd className="flex items-center gap-1 font-mono font-medium">
              {accountNumber
                ? formatBankAccountNumber(accountNumber)
                : topup.receiverAccountName || "—"}
              {accountNumber ? (
                <CopyButton value={accountNumber} label="เลขบัญชี" />
              ) : null}
            </dd>
          </div>
          <div>
            <dt className="text-muted-foreground">จำนวนเงิน</dt>
            <dd className="text-xl font-semibold tabular-nums">
              {formatBalance(topup.requestedSatang ?? topup.creditedSatang)}
            </dd>
          </div>
          <div>
            <dt className="text-muted-foreground">เลขอ้างอิงรายการ</dt>
            <dd className="flex items-center gap-1 font-mono text-xs break-all">
              {topup.reference}
              <CopyButton value={topup.reference} label="เลขอ้างอิง" />
            </dd>
          </div>
          <div>
            <dt className="text-muted-foreground">โอนภายใน</dt>
            <dd>
              {topup.expiresAt ? formatBangkokDateTime(topup.expiresAt) : "—"}
            </dd>
          </div>
        </dl>
        <p className="mt-3 flex items-start gap-2 rounded-xl bg-muted/60 p-3 text-xs text-muted-foreground">
          <Clock3 aria-hidden className="mt-0.5 size-3.5 shrink-0" />
          โอนให้ตรงบัญชีและจำนวนเงิน แล้วอัปโหลดสลิปด้านล่าง ระบบจะไม่เพิ่มยอดเงินจนกว่าการตรวจสอบจะผ่าน
        </p>
      </div>

      {topup.status === "pending" ? (
        <SlipUploader topup={topup} onUpdated={onUpdated} />
      ) : (
        <Alert>
          <Clock3 aria-hidden />
          <AlertTitle>กำลังตรวจสอบสลิป</AlertTitle>
          <AlertDescription>
            {topup.verificationMessage ||
              "ระบบกำลังรอผลจากผู้ให้บริการตรวจสลิป กรุณารอสักครู่"}
          </AlertDescription>
        </Alert>
      )}

      {retryError ? (
        <Alert variant="destructive">
          <CircleAlert aria-hidden />
          <AlertDescription>{retryError}</AlertDescription>
        </Alert>
      ) : null}
      {topup.status === "processing" && topup.canRetry ? (
        <Button variant="outline" onClick={() => void retry()} disabled={retrying}>
          {retrying ? (
            <Spinner data-icon="inline-start" />
          ) : (
            <RotateCcw data-icon="inline-start" />
          )}
          ลองตรวจสอบอีกครั้ง
        </Button>
      ) : null}
      <p className="text-xs text-muted-foreground">
        ผู้รับเงิน: {topup.receiverAccountName || "—"}
        {topup.receiverAccountNumber
          ? ` · ${maskAccountNumber(topup.receiverAccountNumber)}`
          : ""}
      </p>
    </div>
  );
}
