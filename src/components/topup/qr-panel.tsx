"use client";

import { useEffect, useState } from "react";
import Link from "next/link";
import {
  CircleAlert,
  CircleCheck,
  Clock3,
  Download,
  History,
  QrCode,
  RotateCcw,
} from "lucide-react";
import { Alert, AlertDescription, AlertTitle } from "@/components/ui/alert";
import { Badge } from "@/components/ui/badge";
import { Button } from "@/components/ui/button";
import { Spinner } from "@/components/ui/spinner";
import { formatBalance } from "@/lib/money";
import {
  formatBangkokDateTime,
} from "@/lib/date-range";
import type { TopupDetail, TopupSlipResponse } from "@/lib/topup/types";
import { SlipUploader } from "@/components/topup/slip-uploader";
import {
  clientErrorMessage,
  topupFetch,
} from "@/components/topup/client";

function useNow(active: boolean) {
  const [now, setNow] = useState(() => Date.now());
  useEffect(() => {
    if (!active) return;
    const timer = window.setInterval(() => setNow(Date.now()), 1_000);
    return () => window.clearInterval(timer);
  }, [active]);
  return now;
}

function formatCountdown(milliseconds: number) {
  const seconds = Math.max(0, Math.floor(milliseconds / 1_000));
  const minutes = Math.floor(seconds / 60);
  return `${minutes}:${String(seconds % 60).padStart(2, "0")}`;
}

export function QrPanel({
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
  const active = topup.status === "pending" || topup.status === "processing";
  const now = useNow(active);
  const expired =
    topup.status === "expired" ||
    (topup.status === "pending" &&
      topup.expiresAt !== null &&
      topup.expiresAt <= now);
  const countdown =
    topup.expiresAt !== null ? topup.expiresAt - now : 0;

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

  if (topup.status === "failed") {
    return (
      <div className="flex flex-col gap-3">
        <Alert variant="destructive">
          <CircleAlert aria-hidden />
          <AlertTitle>ตรวจสอบสลิปไม่สำเร็จ</AlertTitle>
          <AlertDescription>
            {topup.failureMessage || topup.verificationMessage || "ข้อมูลไม่ตรงกับรายการ"}
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

  return (
    <div className="flex flex-col gap-5">
      <div className="flex flex-wrap items-center justify-between gap-2">
        <div className="flex items-center gap-2">
          <QrCode aria-hidden className="size-5 text-primary" />
          <h3 className="text-lg font-semibold">สแกน QR PromptPay</h3>
        </div>
        {topup.status === "pending" && !expired ? (
          <Badge variant="busy" className="gap-1.5 tabular-nums">
            <Clock3 aria-hidden />
            หมดอายุใน {formatCountdown(countdown)}
          </Badge>
        ) : (
          <Badge variant={expired ? "secondary" : "busy"}>
            {expired ? "หมดอายุ" : "กำลังตรวจสอบ"}
          </Badge>
        )}
      </div>

      <div className="grid gap-5 md:grid-cols-[minmax(0,320px)_minmax(0,1fr)] md:items-start">
        <div className="rounded-3xl border bg-white p-3 shadow-sm">
          {topup.qrImageDataUrl ? (
            // eslint-disable-next-line @next/next/no-img-element
            <img
              src={topup.qrImageDataUrl}
              alt={`QR PromptPay สำหรับเติมเงิน ${formatBalance(topup.requestedSatang ?? 0)}`}
              className="mx-auto aspect-square w-full max-w-[320px] object-contain"
            />
          ) : (
            <div className="flex aspect-square items-center justify-center text-sm text-muted-foreground">
              ยังไม่มีรูป QR
            </div>
          )}
        </div>
        <div className="flex flex-col gap-4">
          <dl className="grid gap-3 text-sm">
            <div>
              <dt className="text-muted-foreground">จำนวนเงิน</dt>
              <dd className="text-2xl font-semibold tabular-nums">
                {formatBalance(topup.requestedSatang ?? topup.creditedSatang)}
              </dd>
            </div>
            <div>
              <dt className="text-muted-foreground">บัญชีผู้รับ</dt>
              <dd className="font-medium">
                {topup.receiverAccountName || "—"}
                {topup.promptpayNumberMasked
                  ? ` · ${topup.promptpayNumberMasked}`
                  : ""}
              </dd>
            </div>
            <div>
              <dt className="text-muted-foreground">เลขอ้างอิง</dt>
              <dd className="font-mono text-xs break-all">{topup.reference}</dd>
            </div>
          </dl>
          {topup.qrImageDataUrl ? (
            <Button
              variant="outline"
              nativeButton={false}
              render={
                <a
                  href={topup.qrImageDataUrl}
                  download={`promptpay-${topup.reference}.png`}
                />
              }
            >
              <Download data-icon="inline-start" />
              บันทึกรูป QR
            </Button>
          ) : null}
          {expired ? (
            <Alert>
              <Clock3 aria-hidden />
              <AlertTitle>QR หมดอายุแล้ว</AlertTitle>
              <AlertDescription>
                ระบบไม่เพิ่มยอดเงินจากรายการที่หมดอายุ กรุณาสร้างรายการใหม่
              </AlertDescription>
            </Alert>
          ) : topup.status === "pending" ? (
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
          {topup.createdAt ? (
            <p className="text-xs text-muted-foreground">
              สร้างเมื่อ {formatBangkokDateTime(topup.createdAt)}
            </p>
          ) : null}
        </div>
      </div>
    </div>
  );
}
