"use client";

import { useCallback, useEffect, useState } from "react";
import {
  CircleAlert,
  CircleCheck,
  ImageIcon,
  ShieldCheck,
} from "lucide-react";
import { Alert, AlertDescription, AlertTitle } from "@/components/ui/alert";
import { Badge } from "@/components/ui/badge";
import { Button } from "@/components/ui/button";
import {
  Dialog,
  DialogContent,
  DialogDescription,
  DialogHeader,
  DialogTitle,
} from "@/components/ui/dialog";
import { Skeleton } from "@/components/ui/skeleton";
import { Spinner } from "@/components/ui/spinner";
import { formatBangkokDateTime } from "@/lib/date-range";
import { formatBalance } from "@/lib/money";
import { maskAccountNumber } from "@/lib/topup/amount";
import type { TopupStatus } from "@/lib/topup/constants";
import type {
  AdminTopupDetail,
  TopupAttemptView,
  TopupEventView,
  TopupSlipResponse,
} from "@/lib/topup/types";
import {
  ChannelBadge,
  StatusBadge,
  VerificationBadge,
  topupStatusLabel,
  type BadgeTone,
} from "@/components/topup/topup-display";
import { clientErrorMessage, topupFetch } from "@/components/topup/client";
import { entryAmount } from "@/components/admin/topups/topup-labels";

const PROMPTPAY_TYPE_LABELS: Record<string, string> = {
  msisdn: "เบอร์โทรศัพท์",
  natId: "เลขบัตรประชาชน",
};

const PROVIDER_LABELS: Record<string, string> = {
  easyslip: "EasySlip",
  slipok: "SlipOK",
  truemoney: "TrueMoney",
};

const ATTEMPT_OUTCOME_LABELS: Record<string, string> = {
  verified: "ตรวจสอบผ่าน",
  completed: "สำเร็จ",
  pending: "รอดำเนินการ",
  processing: "กำลังตรวจสอบ",
  unknown: "ไม่ทราบผล",
  timeout: "หมดเวลาเชื่อมต่อ",
  network: "เชื่อมต่อไม่สำเร็จ",
  "invalid-response": "ผลตอบกลับไม่ถูกต้อง",
  rejected: "ถูกปฏิเสธ",
  rejected_slip: "สลิปไม่ผ่าน",
  rate_limited: "ถูกจำกัดอัตราการเรียก",
  RATE_LIMITED: "ถูกจำกัดอัตราการเรียก",
  not_configured: "ยังไม่ได้ตั้งค่าผู้ให้บริการ",
  missing_slip: "ไม่พบไฟล์สลิป",
};

const ACTOR_LABELS: Record<TopupEventView["actor"], string> = {
  system: "ระบบ",
  user: "ผู้ใช้",
  admin: "ผู้ดูแล",
};

function attemptOutcomeLabel(outcome: string): string {
  return ATTEMPT_OUTCOME_LABELS[outcome] ?? outcome;
}

function attemptOutcomeTone(outcome: string): BadgeTone {
  if (["verified", "completed"].includes(outcome)) return "available";
  if (["pending", "processing", "unknown"].includes(outcome)) return "busy";
  if (
    ["timeout", "network", "rejected", "rejected_slip", "missing_slip", "not_configured"].includes(
      outcome,
    ) ||
    /RATE_LIMIT/i.test(outcome)
  ) {
    return "destructive";
  }
  return "outline";
}

type VerifyNotice = {
  tone: "success" | "failure" | "info";
  message: string;
};

/**
 * Admin-only transaction inspector. It can re-run slip verification for stuck
 * processing rows but never credits or approves money manually.
 */
export function TopupDetailDialog({
  topupId,
  onClose,
}: {
  topupId: string | null;
  onClose: () => void;
}) {
  const [detail, setDetail] = useState<AdminTopupDetail | null>(null);
  const [loading, setLoading] = useState(topupId !== null);
  const [error, setError] = useState("");
  const [reloadKey, setReloadKey] = useState(0);
  const [verifying, setVerifying] = useState(false);
  const [notice, setNotice] = useState<VerifyNotice | null>(null);
  const [verifyError, setVerifyError] = useState("");

  const open = topupId !== null;

  useEffect(() => {
    if (!topupId) return;
    let cancelled = false;
    topupFetch<{ topup: AdminTopupDetail }>(
      `/api/admin/topups/${encodeURIComponent(topupId)}`,
    )
      .then((payload) => {
        if (!cancelled) setDetail(payload.topup);
      })
      .catch((cause) => {
        if (!cancelled) {
          setError(clientErrorMessage(cause));
          setDetail(null);
        }
      })
      .finally(() => {
        if (!cancelled) setLoading(false);
      });
    return () => {
      cancelled = true;
    };
  }, [topupId, reloadKey]);

  const refreshDetail = useCallback(() => {
    setLoading(true);
    setError("");
    setReloadKey((current) => current + 1);
  }, []);

  async function reverify() {
    if (!topupId || verifying) return;
    setVerifying(true);
    setVerifyError("");
    setNotice(null);
    try {
      const result = await topupFetch<TopupSlipResponse>(
        `/api/admin/topups/${encodeURIComponent(topupId)}/verify`,
        { method: "POST" },
      );
      setNotice({
        tone:
          result.outcome === "completed"
            ? "success"
            : result.outcome === "failed"
              ? "failure"
              : "info",
        message: result.message || "ส่งคำขอตรวจสอบสลิปใหม่แล้ว",
      });
      refreshDetail();
    } catch (cause) {
      setVerifyError(clientErrorMessage(cause));
    } finally {
      setVerifying(false);
    }
  }

  return (
    <Dialog
      open={open}
      onOpenChange={(next) => {
        if (!next) onClose();
      }}
    >
      <DialogContent
        data-lenis-prevent
        className="max-h-[90dvh] overflow-y-auto sm:max-w-3xl"
      >
        <DialogHeader>
          <DialogTitle>รายละเอียดรายการเติมเงิน</DialogTitle>
          <DialogDescription>
            {detail
              ? `${detail.reference} · ${detail.userName || detail.userEmail}`
              : "กำลังโหลดรายละเอียดรายการ"}
          </DialogDescription>
        </DialogHeader>

        {loading && !detail ? (
          <div className="flex flex-col gap-3" aria-label="กำลังโหลดรายละเอียดรายการ">
            <Skeleton className="h-6 w-52" />
            <Skeleton className="h-28 w-full" />
            <Skeleton className="h-40 w-full" />
          </div>
        ) : error && !detail ? (
          <Alert variant="destructive">
            <CircleAlert aria-hidden />
            <AlertTitle>โหลดรายละเอียดไม่สำเร็จ</AlertTitle>
            <AlertDescription>
              <p>{error}</p>
              <Button
                type="button"
                variant="outline"
                size="sm"
                className="mt-2"
                onClick={refreshDetail}
              >
                ลองใหม่
              </Button>
            </AlertDescription>
          </Alert>
        ) : detail ? (
          <DetailBody
            detail={detail}
            verifying={verifying}
            notice={notice}
            verifyError={verifyError}
            onReverify={() => void reverify()}
          />
        ) : null}
      </DialogContent>
    </Dialog>
  );
}

function DetailBody({
  detail,
  verifying,
  notice,
  verifyError,
  onReverify,
}: {
  detail: AdminTopupDetail;
  verifying: boolean;
  notice: VerifyNotice | null;
  verifyError: string;
  onReverify: () => void;
}) {
  const amount = entryAmount(detail);
  const canReverify =
    detail.canRetry || (detail.status === "processing" && detail.attempts.length > 0);
  const hasReceiver = Boolean(
    detail.bankName ||
      detail.receiverAccountName ||
      detail.receiverAccountNumber ||
      detail.promptpayNumberMasked ||
      detail.truemoneyPhoneMasked,
  );

  return (
    <div className="flex flex-col gap-5 text-sm">
      <div className="flex flex-wrap items-center gap-2">
        <StatusBadge status={detail.status} />
        <ChannelBadge channel={detail.channel} />
        <VerificationBadge status={detail.verificationStatus} />
      </div>

      {notice ? (
        <Alert variant={notice.tone === "failure" ? "destructive" : "default"}>
          {notice.tone === "success" ? <CircleCheck aria-hidden /> : <CircleAlert aria-hidden />}
          <AlertTitle>ผลการตรวจสอบสลิป</AlertTitle>
          <AlertDescription>{notice.message}</AlertDescription>
        </Alert>
      ) : null}

      {verifyError ? (
        <Alert variant="destructive">
          <CircleAlert aria-hidden />
          <AlertTitle>ตรวจสอบสลิปไม่สำเร็จ</AlertTitle>
          <AlertDescription>{verifyError}</AlertDescription>
        </Alert>
      ) : null}

      {detail.failureMessage ? (
        <Alert variant="destructive">
          <CircleAlert aria-hidden />
          <AlertTitle>สาเหตุที่ไม่สำเร็จ</AlertTitle>
          <AlertDescription>{detail.failureMessage}</AlertDescription>
        </Alert>
      ) : null}

      <section aria-label="สรุปรายการ">
        <SectionTitle>สรุปรายการ</SectionTitle>
        <dl className="grid gap-3 rounded-2xl border bg-card p-4 sm:grid-cols-2">
          <DetailRow label="เลขอ้างอิง">
            <span className="font-mono text-xs break-all">{detail.reference}</span>
          </DetailRow>
          <DetailRow label="จำนวนที่ขอ">
            {detail.requestedSatang === null ? "—" : formatBalance(detail.requestedSatang)}
          </DetailRow>
          <DetailRow label="ยอดที่เติมเข้าระบบจริง">
            <span className="font-medium">{formatBalance(detail.creditedSatang)}</span>
          </DetailRow>
          <DetailRow label="ยอดรวมของรายการ">
            {amount === null ? "—" : formatBalance(amount)}
          </DetailRow>
          <DetailRow label="สร้างเมื่อ">{formatBangkokDateTime(detail.createdAt)}</DetailRow>
          <DetailRow label="อัปเดตล่าสุด">{formatBangkokDateTime(detail.updatedAt)}</DetailRow>
          {detail.completedAt !== null ? (
            <DetailRow label="เติมเงินสำเร็จเมื่อ">
              {formatBangkokDateTime(detail.completedAt)}
            </DetailRow>
          ) : null}
          {detail.expiresAt !== null ? (
            <DetailRow label="หมดอายุ">{formatBangkokDateTime(detail.expiresAt)}</DetailRow>
          ) : null}
          {detail.verifiedAt !== null ? (
            <DetailRow label="ตรวจสอบสลิปเมื่อ">
              {formatBangkokDateTime(detail.verifiedAt)}
            </DetailRow>
          ) : null}
        </dl>
      </section>

      <section aria-label="ข้อมูลผู้ใช้">
        <SectionTitle>ผู้ใช้</SectionTitle>
        <dl className="grid gap-3 rounded-2xl border bg-card p-4 sm:grid-cols-2">
          <DetailRow label="ชื่อผู้ใช้">{detail.userName || "—"}</DetailRow>
          <DetailRow label="อีเมล">{detail.userEmail || "—"}</DetailRow>
          <DetailRow label="รหัสผู้ใช้">
            <span className="font-mono text-xs break-all">{detail.userId}</span>
          </DetailRow>
          <DetailRow label="เลขอ้างอิงฝั่งผู้ให้บริการ">
            <span className="font-mono text-xs break-all">
              {detail.providerRequestId || "—"}
            </span>
          </DetailRow>
        </dl>
      </section>

      {hasReceiver ? (
        <section aria-label="ข้อมูลผู้รับเงิน">
          <SectionTitle>ผู้รับเงิน (ข้อมูล ณ เวลาทำรายการ)</SectionTitle>
          <dl className="grid gap-3 rounded-2xl border bg-card p-4 sm:grid-cols-2">
            {detail.bankName ? (
              <DetailRow label="ธนาคาร">
                {detail.bankName}
                {detail.bankCode ? ` (${detail.bankCode})` : ""}
              </DetailRow>
            ) : null}
            {detail.receiverAccountName ? (
              <DetailRow label="ชื่อบัญชีผู้รับ">{detail.receiverAccountName}</DetailRow>
            ) : null}
            {detail.receiverAccountNumber ? (
              <DetailRow label="เลขบัญชีผู้รับ">
                <span className="font-mono text-xs">
                  {maskAccountNumber(detail.receiverAccountNumber)}
                </span>
              </DetailRow>
            ) : null}
            {detail.promptpayType || detail.promptpayNumberMasked ? (
              <DetailRow label="PromptPay">
                {PROMPTPAY_TYPE_LABELS[detail.promptpayType ?? ""] ?? "PromptPay"}
                {detail.promptpayNumberMasked ? ` · ${detail.promptpayNumberMasked}` : ""}
              </DetailRow>
            ) : null}
            {detail.truemoneyPhoneMasked ? (
              <DetailRow label="เบอร์ TrueMoney">
                <span className="font-mono text-xs">{detail.truemoneyPhoneMasked}</span>
              </DetailRow>
            ) : null}
          </dl>
        </section>
      ) : null}

      {detail.qrImageDataUrl ? (
        <section aria-label="QR พร้อมเพย์">
          <SectionTitle>QR PromptPay</SectionTitle>
          <div className="flex flex-col gap-2">
            <div className="w-fit rounded-2xl border bg-white p-2">
              {/* eslint-disable-next-line @next/next/no-img-element */}
              <img
                src={detail.qrImageDataUrl}
                alt={`QR PromptPay ${detail.reference}`}
                className="size-48 object-contain"
              />
            </div>
            {detail.qrExpiresAt !== null ? (
              <p className="text-xs text-muted-foreground">
                QR หมดอายุ {formatBangkokDateTime(detail.qrExpiresAt)}
              </p>
            ) : null}
          </div>
        </section>
      ) : null}

      {detail.slipImageUrl ? (
        <section aria-label="รูปสลิป">
          <SectionTitle>
            <span className="inline-flex items-center gap-2">
              <ImageIcon aria-hidden className="size-4" />
              รูปสลิปที่อัปโหลด
            </span>
          </SectionTitle>
          {/* Same-origin cookies authorise the admin slip route; no auth header needed. */}
          {/* eslint-disable-next-line @next/next/no-img-element */}
          <img
            src={detail.slipImageUrl}
            alt={`สลิป ${detail.reference}`}
            className="max-h-72 w-fit max-w-full rounded-xl border object-contain"
          />
        </section>
      ) : null}

      <section aria-label="ผลการตรวจสอบสลิป">
        <SectionTitle>ผลการตรวจสอบสลิป</SectionTitle>
        <dl className="grid gap-3 rounded-2xl border bg-card p-4 sm:grid-cols-2">
          <DetailRow label="สถานะการตรวจสอบ">
            <VerificationBadge status={detail.verificationStatus} />
          </DetailRow>
          <DetailRow label="ผู้ให้บริการตรวจสลิป">
            {detail.slipProvider ? (PROVIDER_LABELS[detail.slipProvider] ?? detail.slipProvider) : "—"}
          </DetailRow>
          <DetailRow label="รหัสผลการตรวจสอบ">
            <span className="font-mono text-xs break-all">
              {detail.verificationCode || "—"}
            </span>
          </DetailRow>
          <DetailRow label="ข้อความผลการตรวจสอบ">
            {detail.verificationMessage || "—"}
          </DetailRow>
          <DetailRow label="เลขอ้างอิงสลิป">
            <span className="font-mono text-xs break-all">{detail.slipTransRef || "—"}</span>
          </DetailRow>
          <DetailRow label="ยอดเงินในสลิป">
            {detail.slipAmountSatang === null ? "—" : formatBalance(detail.slipAmountSatang)}
          </DetailRow>
          <DetailRow label="ชื่อผู้โอนในสลิป">{detail.slipSenderName || "—"}</DetailRow>
          <DetailRow label="ชื่อผู้รับในสลิป">{detail.slipReceiverName || "—"}</DetailRow>
          <DetailRow label="บัญชีผู้รับในสลิป">
            <span className="font-mono text-xs">{detail.slipReceiverAccountMasked || "—"}</span>
          </DetailRow>
          <DetailRow label="จำนวนครั้งที่ตรวจสอบ">
            {detail.attemptsCount.toLocaleString("th-TH")}
          </DetailRow>
        </dl>
      </section>

      {canReverify ? (
        <section
          aria-label="ตรวจสอบสลิปซ้ำ"
          className="flex flex-col gap-2 rounded-2xl border bg-card p-4"
        >
          <SectionTitle>ตรวจสอบสลิปซ้ำ</SectionTitle>
          <p className="text-xs text-muted-foreground">
            สำหรับรายการที่ค้างสถานะกำลังตรวจสอบ ระบบจะเรียกผู้ให้บริการอีกครั้งและบันทึกผล
            อัตโนมัติ ไม่มีการเพิ่มยอดเงินด้วยมือ
          </p>
          <Button
            type="button"
            variant="outline"
            className="w-fit"
            disabled={verifying}
            onClick={onReverify}
          >
            {verifying ? (
              <Spinner data-icon="inline-start" />
            ) : (
              <ShieldCheck data-icon="inline-start" />
            )}
            ตรวจสอบสลิปอีกครั้ง
          </Button>
        </section>
      ) : null}

      <section aria-label="ประวัติการเรียกผู้ให้บริการ">
        <SectionTitle>ประวัติการเรียกผู้ให้บริการ</SectionTitle>
        {detail.attempts.length === 0 ? (
          <p className="text-sm text-muted-foreground">ยังไม่มีการเรียกผู้ให้บริการ</p>
        ) : (
          <ol className="flex flex-col gap-3">
            {detail.attempts.map((attempt) => (
              <AttemptCard key={attempt.id} attempt={attempt} />
            ))}
          </ol>
        )}
      </section>

      <section aria-label="ไทม์ไลน์รายการ">
        <SectionTitle>ไทม์ไลน์รายการ</SectionTitle>
        {detail.events.length === 0 ? (
          <p className="text-sm text-muted-foreground">ยังไม่มีไทม์ไลน์รายการ</p>
        ) : (
          <ol className="flex flex-col gap-2">
            {detail.events.map((event) => (
              <li key={event.id} className="flex gap-3 rounded-xl border p-3">
                <span className="mt-1 size-2 shrink-0 rounded-full bg-primary" aria-hidden />
                <div className="min-w-0">
                  <p className="font-medium">{eventTitle(event)}</p>
                  <p className="text-xs text-muted-foreground">
                    {formatBangkokDateTime(event.createdAt)} · {ACTOR_LABELS[event.actor] ?? event.actor}
                    {event.reason ? ` · ${event.reason}` : ""}
                  </p>
                </div>
              </li>
            ))}
          </ol>
        )}
      </section>
    </div>
  );
}

function AttemptCard({ attempt }: { attempt: TopupAttemptView }) {
  return (
    <li className="flex flex-col gap-2 rounded-xl border p-3">
      <div className="flex flex-wrap items-center gap-2">
        <Badge variant="outline">ครั้งที่ {attempt.attemptNo}</Badge>
        <Badge variant="secondary">
          {PROVIDER_LABELS[attempt.provider] ?? attempt.provider}
        </Badge>
        <Badge variant={attemptOutcomeTone(attempt.outcome)}>
          {attemptOutcomeLabel(attempt.outcome)}
        </Badge>
        <span className="text-xs text-muted-foreground">
          {formatBangkokDateTime(attempt.createdAt)}
        </span>
        {attempt.durationMs !== null ? (
          <span className="text-xs text-muted-foreground">
            ใช้เวลา {attempt.durationMs.toLocaleString("th-TH")} มิลลิวินาที
          </span>
        ) : null}
      </div>
      <dl className="grid gap-x-4 gap-y-1 text-xs sm:grid-cols-2">
        <div className="flex gap-1">
          <dt className="text-muted-foreground">HTTP</dt>
          <dd className="font-mono">{attempt.httpStatus ?? "—"}</dd>
        </div>
        <div className="flex gap-1">
          <dt className="text-muted-foreground">เลขอ้างอิงสลิป</dt>
          <dd className="font-mono break-all">{attempt.transRef || "—"}</dd>
        </div>
        <div className="flex gap-1">
          <dt className="text-muted-foreground">ยอดเงิน</dt>
          <dd className="font-mono tabular-nums">
            {attempt.amountSatang === null ? "—" : formatBalance(attempt.amountSatang)}
          </dd>
        </div>
        <div className="flex gap-1">
          <dt className="text-muted-foreground">รหัสข้อผิดพลาด</dt>
          <dd className="font-mono break-all">{attempt.errorCode || "—"}</dd>
        </div>
        {attempt.errorMessage ? (
          <div className="flex gap-1 sm:col-span-2">
            <dt className="text-muted-foreground">ข้อความข้อผิดพลาด</dt>
            <dd className="break-words">{attempt.errorMessage}</dd>
          </div>
        ) : null}
        {attempt.retryAfterSeconds !== null ? (
          <div className="flex gap-1">
            <dt className="text-muted-foreground">ลองใหม่หลัง</dt>
            <dd>{attempt.retryAfterSeconds.toLocaleString("th-TH")} วินาที</dd>
          </div>
        ) : null}
      </dl>
      {attempt.requestSummary ? (
        <div>
          <p className="text-xs font-medium">ข้อมูลคำขอ</p>
          <pre className="mt-1 max-h-40 overflow-auto rounded-xl bg-muted p-2 font-mono text-[11px] leading-relaxed break-all whitespace-pre-wrap">
            {attempt.requestSummary}
          </pre>
        </div>
      ) : null}
      {attempt.responseExcerpt ? (
        <div>
          <p className="text-xs font-medium">ตัวอย่างผลตอบกลับจากผู้ให้บริการ</p>
          <pre className="mt-1 max-h-48 overflow-auto rounded-xl bg-muted p-2 font-mono text-[11px] leading-relaxed break-all whitespace-pre-wrap">
            {attempt.responseExcerpt}
          </pre>
        </div>
      ) : null}
    </li>
  );
}

function eventTitle(event: TopupEventView): string {
  const target = topupStatusLabel(event.toStatus as TopupStatus);
  if (!event.fromStatus) return target;
  return `${topupStatusLabel(event.fromStatus as TopupStatus)} → ${target}`;
}

function SectionTitle({ children }: { children: React.ReactNode }) {
  return <h3 className="mb-2 text-sm font-semibold">{children}</h3>;
}

function DetailRow({
  label,
  children,
}: {
  label: string;
  children: React.ReactNode;
}) {
  return (
    <div className="flex min-w-0 flex-col gap-0.5">
      <dt className="text-xs text-muted-foreground">{label}</dt>
      <dd className="min-w-0 break-words">{children}</dd>
    </div>
  );
}
