"use client";

import { useEffect, useState } from "react";
import Link from "next/link";
import { CircleAlert, ExternalLink, ImageIcon } from "lucide-react";
import { Alert, AlertDescription, AlertTitle } from "@/components/ui/alert";
import { Button } from "@/components/ui/button";
import {
  Dialog,
  DialogContent,
  DialogDescription,
  DialogHeader,
  DialogTitle,
} from "@/components/ui/dialog";
import { DialogSkeleton, LoadingRegion } from "@/components/loading";
import { formatBalance } from "@/lib/money";
import { formatBangkokDateTime } from "@/lib/date-range";
import type { TopupDetail } from "@/lib/topup/types";
import {
  ChannelBadge,
  StatusBadge,
  VerificationBadge,
} from "@/components/topup/topup-display";
import {
  clientErrorMessage,
  topupFetch,
} from "@/components/topup/client";

export function TopupDetailDialog({
  topupId,
  open,
  onOpenChange,
}: {
  topupId: string | null;
  open: boolean;
  onOpenChange: (open: boolean) => void;
}) {
  const [detail, setDetail] = useState<TopupDetail | null>(null);
  const [loading, setLoading] = useState(false);
  const [error, setError] = useState("");

  useEffect(() => {
    if (!open || !topupId) return;
    const controller = new AbortController();
    const timer = window.setTimeout(() => {
      setLoading(true);
      setError("");
      setDetail(null);
      void topupFetch<{ topup: TopupDetail }>(`/api/topups/${topupId}`, {
        signal: controller.signal,
      })
        .then((data) => {
          if (!controller.signal.aborted) setDetail(data.topup);
        })
        .catch((cause) => {
          if (!controller.signal.aborted) setError(clientErrorMessage(cause));
        })
        .finally(() => {
          if (!controller.signal.aborted) setLoading(false);
        });
    }, 0);
    return () => {
      window.clearTimeout(timer);
      controller.abort();
    };
  }, [open, topupId]);

  return (
    <Dialog open={open} onOpenChange={onOpenChange}>
      <DialogContent className="max-h-[90vh] w-[calc(100%-2rem)] max-w-2xl overflow-y-auto">
        <DialogHeader>
          <DialogTitle>รายละเอียดการเติมเงิน</DialogTitle>
          <DialogDescription>
            {detail ? (
              `เลขอ้างอิง ${detail.reference}`
            ) : (
              <span className="sr-only">กำลังโหลดรายละเอียดรายการ</span>
            )}
          </DialogDescription>
        </DialogHeader>

        {loading ? (
          <LoadingRegion label="กำลังโหลดรายละเอียดรายการ">
            <DialogSkeleton />
          </LoadingRegion>
        ) : error ? (
          <Alert variant="destructive">
            <CircleAlert aria-hidden />
            <AlertTitle>โหลดรายละเอียดไม่สำเร็จ</AlertTitle>
            <AlertDescription>{error}</AlertDescription>
          </Alert>
        ) : detail ? (
          <TopupDetailBody detail={detail} />
        ) : null}
      </DialogContent>
    </Dialog>
  );
}

function TopupDetailBody({ detail }: { detail: TopupDetail }) {
  const amount = detail.creditedSatang > 0 ? detail.creditedSatang : detail.requestedSatang;
  return (
    <div className="flex flex-col gap-5 text-sm">
      <div className="flex flex-wrap items-center gap-2">
        <StatusBadge status={detail.status} />
        <ChannelBadge channel={detail.channel} />
        <VerificationBadge status={detail.verificationStatus} />
      </div>

      <dl className="grid gap-3 rounded-2xl border bg-card p-4 sm:grid-cols-2">
        <div>
          <dt className="text-muted-foreground">จำนวนเงิน</dt>
          <dd className="text-xl font-semibold tabular-nums">
            {amount === null ? "—" : formatBalance(amount)}
          </dd>
        </div>
        <div>
          <dt className="text-muted-foreground">วันที่ทำรายการ</dt>
          <dd>{formatBangkokDateTime(detail.createdAt)}</dd>
        </div>
        <div>
          <dt className="text-muted-foreground">เลขอ้างอิง</dt>
          <dd className="font-mono text-xs break-all">{detail.reference}</dd>
        </div>
        <div>
          <dt className="text-muted-foreground">ผู้รับเงิน</dt>
          <dd>
            {detail.receiverAccountName || detail.bankName || "—"}
            {detail.receiverAccountNumber ? ` · ${detail.receiverAccountNumber}` : ""}
          </dd>
        </div>
        {detail.completedAt ? (
          <div>
            <dt className="text-muted-foreground">เติมเงินสำเร็จเมื่อ</dt>
            <dd>{formatBangkokDateTime(detail.completedAt)}</dd>
          </div>
        ) : null}
        {detail.expiresAt ? (
          <div>
            <dt className="text-muted-foreground">หมดอายุ</dt>
            <dd>{formatBangkokDateTime(detail.expiresAt)}</dd>
          </div>
        ) : null}
      </dl>

      {detail.qrImageDataUrl ? (
        <div className="flex flex-col gap-2">
          <p className="font-medium">QR PromptPay</p>
          <div className="w-fit rounded-2xl border bg-white p-2">
            {/* eslint-disable-next-line @next/next/no-img-element */}
            <img
              src={detail.qrImageDataUrl}
              alt={`QR PromptPay ${detail.reference}`}
              className="size-52 object-contain"
            />
          </div>
        </div>
      ) : null}

      {detail.failureMessage ? (
        <Alert variant="destructive">
          <CircleAlert aria-hidden />
          <AlertTitle>สาเหตุที่ไม่สำเร็จ</AlertTitle>
          <AlertDescription>{detail.failureMessage}</AlertDescription>
        </Alert>
      ) : null}

      {detail.slipImageUrl ? (
        <div className="flex flex-col gap-2">
          <p className="flex items-center gap-2 font-medium">
            <ImageIcon aria-hidden className="size-4" />
            รูปสลิปที่อัปโหลด
          </p>
          {/* eslint-disable-next-line @next/next/no-img-element */}
          <img
            src={detail.slipImageUrl}
            alt={`สลิป ${detail.reference}`}
            className="max-h-72 w-fit max-w-full rounded-xl border object-contain"
          />
        </div>
      ) : null}

      {detail.events.length > 0 ? (
        <div className="flex flex-col gap-2">
          <p className="font-medium">ไทม์ไลน์รายการ</p>
          <ol className="flex flex-col gap-2">
            {detail.events.map((event) => (
              <li key={event.id} className="flex gap-3 rounded-xl border p-3">
                <span className="mt-1 size-2 shrink-0 rounded-full bg-primary" />
                <div className="min-w-0">
                  <p className="font-medium">
                    <StatusLabelText status={event.toStatus} />
                  </p>
                  <p className="text-xs text-muted-foreground">
                    {formatBangkokDateTime(event.createdAt)}
                    {event.reason ? ` · ${event.reason}` : ""}
                  </p>
                </div>
              </li>
            ))}
          </ol>
        </div>
      ) : null}

      {detail.canResume || detail.status === "processing" || detail.status === "pending" ? (
        <Button
          nativeButton={false}
          render={<Link href={`/topup?tx=${encodeURIComponent(detail.id)}`} />}
        >
          <ExternalLink data-icon="inline-start" />
          เปิดหน้ารายการเพื่อดำเนินการต่อ
        </Button>
      ) : null}
    </div>
  );
}

function StatusLabelText({ status }: { status: string }) {
  const labels: Record<string, string> = {
    pending: "สร้างรายการ / รอชำระเงิน",
    processing: "กำลังตรวจสอบ",
    completed: "เติมเงินสำเร็จ",
    failed: "รายการไม่สำเร็จ",
    expired: "รายการหมดอายุ",
  };
  return <>{labels[status] ?? status}</>;
}
