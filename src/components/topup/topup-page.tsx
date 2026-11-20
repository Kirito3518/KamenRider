"use client";

import { useCallback, useEffect, useState, type ReactNode } from "react";
import Image from "next/image";
import Link from "next/link";
import { useSearchParams } from "next/navigation";
import {
  ChevronRight,
  CircleAlert,
  History,
  Landmark,
  QrCode,
  RefreshCw,
  TicketPercent,
  Wallet,
} from "lucide-react";
import { motion, useReducedMotion } from "motion/react";
import { Alert, AlertDescription, AlertTitle } from "@/components/ui/alert";
import { Badge } from "@/components/ui/badge";
import { Button } from "@/components/ui/button";
import { Card, CardContent, CardHeader, CardTitle } from "@/components/ui/card";
import {
  Dialog,
  DialogContent,
  DialogDescription,
  DialogHeader,
  DialogTitle,
} from "@/components/ui/dialog";
import { Skeleton } from "@/components/ui/skeleton";
import { Spinner } from "@/components/ui/spinner";
import { Footer } from "@/components/ui/layout/Footer";
import { Navbar } from "@/components/ui/layout/Navbar";
import { formatBalance } from "@/lib/money";
import { cn } from "@/lib/utils";
import { getBankPreset, getBankPresetByLogoKey } from "@/lib/topup/banks";
import type {
  TopupCreateResponse,
  TopupDetail,
  TopupPublicConfig,
  TopupSlipResponse,
} from "@/lib/topup/types";
import { TrueMoneyForm } from "@/components/topup/truemoney-form";
import { PromptpayForm } from "@/components/topup/promptpay-form";
import { BankTransferForm } from "@/components/topup/bank-transfer-form";
import { CouponForm } from "@/components/topup/coupon-form";
import { QrPanel } from "@/components/topup/qr-panel";
import { BankTransferPanel } from "@/components/topup/bank-transfer-panel";
import { BankLogo } from "@/components/topup/topup-display";
import { clientErrorMessage, topupFetch } from "@/components/topup/client";

type ChannelKey = "truemoney" | "bank" | "coupon";
type DialogKey = "promptpay" | "bank" | "coupon";

export function TopupPage({
  initialBalance,
  initialTopupId,
}: {
  initialBalance: number;
  initialTopupId?: string;
}) {
  const reduceMotion = useReducedMotion();
  const [config, setConfig] = useState<TopupPublicConfig | null>(null);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState("");
  const [balanceSatang, setBalanceSatang] = useState(initialBalance);
  const [selectedChannel, setSelectedChannel] = useState<ChannelKey>("truemoney");
  const [dialog, setDialog] = useState<DialogKey | null>(null);
  const [resume, setResume] = useState<TopupDetail | null>(null);
  const [resumeError, setResumeError] = useState("");

  const loadConfig = useCallback(async (signal?: AbortSignal) => {
    setLoading(true);
    setError("");
    try {
      const data = await topupFetch<TopupPublicConfig>("/api/topups/config", {
        signal,
      });
      if (signal?.aborted) return;
      setConfig(data);
      if (!data.truemoney.enabled) {
        setSelectedChannel(data.bankRail ? "bank" : "coupon");
      }
    } catch (cause) {
      if (!signal?.aborted) setError(clientErrorMessage(cause));
    } finally {
      if (!signal?.aborted) setLoading(false);
    }
  }, []);

  useEffect(() => {
    const controller = new AbortController();
    const timer = window.setTimeout(() => void loadConfig(controller.signal), 0);
    return () => {
      window.clearTimeout(timer);
      controller.abort();
    };
  }, [loadConfig]);

  useEffect(() => {
    if (!initialTopupId) return;
    const controller = new AbortController();
    void topupFetch<{ topup: TopupDetail; balanceSatang: number }>(
      `/api/topups/${initialTopupId}`,
      { signal: controller.signal },
    )
      .then((data) => {
        if (controller.signal.aborted) return;
        setResume(data.topup);
        setBalanceSatang(data.balanceSatang);
      })
      .catch((cause) => {
        if (!controller.signal.aborted) setResumeError(clientErrorMessage(cause));
      });
    return () => controller.abort();
  }, [initialTopupId]);

  function updated(result: TopupSlipResponse) {
    setResume(result.topup);
    setBalanceSatang(result.balanceSatang);
  }

  const bankRail = config?.bankRail ?? null;
  const bankEnabled = bankRail !== null;
  const truemoneyEnabled = Boolean(config?.truemoney.enabled);
  const couponEnabled = Boolean(config?.coupon.enabled);
  const modeLabel =
    bankRail === "bank_transfer" ? "โอนผ่านบัญชีธนาคาร" : "PromptPay QR Code";
  const firstBankAccount = config?.bankTransfer.accounts[0] ?? null;
  const bankPreset =
    getBankPresetByLogoKey(firstBankAccount?.logoKey) ??
    (firstBankAccount ? getBankPreset(firstBankAccount.bankCode) : undefined);

  return (
    <div className="flex min-h-screen flex-col">
      <Navbar />
      <main className="mx-auto w-full max-w-3xl flex-1 px-4 pt-28 pb-16 sm:px-5 sm:pt-32">
        <motion.header
          initial={reduceMotion ? false : { opacity: 0, y: 10 }}
          animate={{ opacity: 1, y: 0 }}
          transition={reduceMotion ? { duration: 0 } : { duration: 0.28 }}
          className="flex flex-col gap-3"
        >
          <div className="flex items-center gap-2 text-xs font-medium text-muted-foreground">
            <span className="flex size-6 items-center justify-center rounded-lg bg-primary/10">
              <Image
                src="/topup/wallet.webp"
                alt=""
                width={18}
                height={18}
                className="size-[18px] object-contain"
              />
            </span>
            กระเป๋าเงิน
          </div>
          <div className="flex flex-wrap items-end justify-between gap-3">
            <div>
              <h1 className="text-3xl font-bold tracking-tight">เติมเงินเข้าระบบ</h1>
              <p className="mt-1 text-sm text-muted-foreground">
                เลือกช่องทางที่สะดวก เติมเงินเข้าระบบได้ทันทีหลังยืนยัน
              </p>
            </div>
            <div className="flex items-center gap-2 rounded-xl border bg-card px-3 py-2 text-sm">
              <Wallet aria-hidden className="size-4 text-muted-foreground" />
              <span className="text-muted-foreground">คงเหลือ</span>
              <span className="font-semibold tabular-nums" aria-live="polite">
                {formatBalance(balanceSatang)}
              </span>
            </div>
          </div>
          <div className="flex flex-wrap items-center gap-2">
            <Button variant="outline" size="sm" nativeButton={false} render={<Link href="/history" />}>
              <History data-icon="inline-start" />
              ประวัติการเติมเงิน
            </Button>
            <Button
              variant="ghost"
              size="sm"
              onClick={() => void loadConfig()}
              disabled={loading}
            >
              {loading ? (
                <Spinner data-icon="inline-start" />
              ) : (
                <RefreshCw data-icon="inline-start" />
              )}
              รีเฟรช
            </Button>
          </div>
        </motion.header>

        {error ? (
          <Alert variant="destructive" className="mt-6">
            <CircleAlert aria-hidden />
            <AlertTitle>โหลดข้อมูลการเติมเงินไม่สำเร็จ</AlertTitle>
            <AlertDescription>
              <p>{error}</p>
              <Button
                variant="outline"
                size="sm"
                className="mt-2"
                onClick={() => void loadConfig()}
              >
                ลองใหม่
              </Button>
            </AlertDescription>
          </Alert>
        ) : null}

        {resumeError ? (
          <Alert variant="destructive" className="mt-6">
            <CircleAlert aria-hidden />
            <AlertTitle>โหลดรายการที่ค้างอยู่ไม่สำเร็จ</AlertTitle>
            <AlertDescription>{resumeError}</AlertDescription>
          </Alert>
        ) : null}

        {resume ? (
          <Card className="mt-6 gap-4 rounded-3xl border shadow-none">
            <CardHeader>
              <CardTitle className="flex items-center gap-2 text-base">
                <RefreshCw aria-hidden className="size-4 text-primary" />
                รายการที่ค้างอยู่
              </CardTitle>
            </CardHeader>
            <CardContent>
              {resume.channel === "promptpay" ? (
                <QrPanel topup={resume} onUpdated={updated} onReset={() => setResume(null)} />
              ) : resume.channel === "bank_transfer" ? (
                <BankTransferPanel topup={resume} onUpdated={updated} onReset={() => setResume(null)} />
              ) : (
                <TrueMoneyResume
                  topup={resume}
                  onUpdated={(next, balance) => {
                    setResume(next);
                    setBalanceSatang(balance);
                  }}
                />
              )}
            </CardContent>
          </Card>
        ) : null}

        {loading && !config ? (
          <div className="mt-6 flex flex-col gap-3">
            <Skeleton className="h-12 w-full rounded-2xl" />
            <Skeleton className="h-72 w-full rounded-3xl" />
          </div>
        ) : config ? (
          <motion.div
            initial={reduceMotion ? false : { opacity: 0, y: 10 }}
            animate={{ opacity: 1, y: 0 }}
            transition={reduceMotion ? { duration: 0 } : { duration: 0.28, delay: 0.05 }}
            className="mt-6"
          >
            <Card className="gap-0 overflow-hidden rounded-3xl border shadow-none">
              <CardHeader className="border-b">
                <CardTitle className="text-base">ช่องทางการเติมเงิน</CardTitle>
              </CardHeader>
              <CardContent className="flex flex-col gap-3 p-4">
                <ChannelRow
                  title={modeLabel}
                  subtitle={
                    bankRail === "bank_transfer"
                      ? "โอนเงินผ่านบัญชีธนาคาร แล้วอัปโหลดสลิป"
                      : "สแกน QR Code แล้วอัปโหลดสลิปเพื่อยืนยัน"
                  }
                  disabled={!bankEnabled}
                  badge={bankEnabled ? "ฟรี" : "ปิด"}
                  icon={
                    bankRail === "bank_transfer" && firstBankAccount ? (
                      <BankLogo
                        bankCode={firstBankAccount.bankCode}
                        bankName={firstBankAccount.bankName}
                        logoKey={firstBankAccount.logoKey}
                        logoDataUrl={firstBankAccount.logoDataUrl}
                        className="size-7 rounded-lg"
                      />
                    ) : bankRail === "bank_transfer" && bankPreset ? (
                      <BankLogo
                        bankCode={bankPreset.code}
                        bankName={bankPreset.name}
                        logoKey={bankPreset.key}
                        className="size-7 rounded-lg"
                      />
                    ) : bankRail === "bank_transfer" ? (
                      <Landmark aria-hidden className="size-6 text-primary" />
                    ) : (
                      <QrCode aria-hidden className="size-6 text-primary" />
                    )
                  }
                  onClick={() => {
                    if (!bankEnabled) return;
                    setDialog(bankRail === "bank_transfer" ? "bank" : "promptpay");
                  }}
                />
                <div
                  className={cn(
                    "overflow-hidden rounded-2xl border transition-colors",
                    selectedChannel === "truemoney" && truemoneyEnabled
                      ? "border-primary/60 bg-primary/5"
                      : "border-border bg-card",
                  )}
                >
                  <ChannelRow
                    title="TrueMoney อั่งเปา"
                    subtitle="เติมเงินผ่าน TrueMoney Wallet"
                    disabled={!truemoneyEnabled}
                    badge={truemoneyEnabled ? "ฟรี" : "ปิด"}
                    selected={selectedChannel === "truemoney" && truemoneyEnabled}
                    icon={
                      <Image
                        src="/topup/wallet.webp"
                        alt=""
                        width={30}
                        height={30}
                        className="size-[30px] object-contain"
                      />
                    }
                    onClick={() => {
                      if (!truemoneyEnabled) return;
                      setSelectedChannel("truemoney");
                    }}
                  />
                  {selectedChannel === "truemoney" && truemoneyEnabled ? (
                    <div className="border-t p-4">
                      <TrueMoneyForm
                        config={config}
                        onBalance={setBalanceSatang}
                        embedded
                      />
                    </div>
                  ) : null}
                </div>

                <ChannelRow
                  title="คูปอง"
                  subtitle="เติมเงินด้วยรหัสคูปอง"
                  disabled={!couponEnabled}
                  badge={couponEnabled ? "ฟรี" : "เร็ว ๆ นี้"}
                  icon={<TicketPercent aria-hidden className="size-6 text-primary" />}
                  onClick={() => setDialog("coupon")}
                />
              </CardContent>
            </Card>

            <p className="mt-4 text-center text-xs text-muted-foreground">
              ระบบตรวจสอบยอดเงินและบัญชีผู้รับก่อนเพิ่มเงินทุกครั้ง · เพิ่มยอดเพียงครั้งเดียว
            </p>
          </motion.div>
        ) : null}
      </main>
      <Footer />

      {config ? (
        <>
          <Dialog
            open={dialog === "promptpay"}
            onOpenChange={(open) => setDialog(open ? "promptpay" : null)}
          >
            <DialogContent className="max-h-[92dvh] overflow-y-auto sm:max-w-lg">
              <DialogHeader>
                <DialogTitle>เติมเงินด้วย PromptPay QR Code</DialogTitle>
                <DialogDescription>
                  กรอกจำนวนเงิน สร้าง QR แล้วอัปโหลดสลิปเพื่อให้ระบบตรวจสอบ
                </DialogDescription>
              </DialogHeader>
              <PromptpayForm config={config} onBalance={setBalanceSatang} embedded />
            </DialogContent>
          </Dialog>

          <Dialog
            open={dialog === "bank"}
            onOpenChange={(open) => setDialog(open ? "bank" : null)}
          >
            <DialogContent className="max-h-[92dvh] overflow-y-auto sm:max-w-lg">
              <DialogHeader>
                <DialogTitle>เติมเงินด้วยบัญชีธนาคาร</DialogTitle>
                <DialogDescription>
                  เลือกบัญชี จำนวนเงิน แล้วอัปโหลดสลิปหลังโอน
                </DialogDescription>
              </DialogHeader>
              <BankTransferForm config={config} onBalance={setBalanceSatang} embedded />
            </DialogContent>
          </Dialog>

          <Dialog
            open={dialog === "coupon"}
            onOpenChange={(open) => setDialog(open ? "coupon" : null)}
          >
            <DialogContent className="max-h-[92dvh] overflow-y-auto sm:max-w-lg">
              <DialogHeader>
                <DialogTitle>ใช้รหัสคูปอง</DialogTitle>
                <DialogDescription>กรอกรหัสคูปองเพื่อรับสิทธิ์เติมเงิน</DialogDescription>
              </DialogHeader>
              <CouponForm config={config} embedded />
            </DialogContent>
          </Dialog>
        </>
      ) : null}
    </div>
  );
}

function ChannelRow({
  title,
  subtitle,
  icon,
  badge,
  disabled = false,
  selected = false,
  onClick,
}: {
  title: string;
  subtitle: string;
  icon: ReactNode;
  badge: string;
  disabled?: boolean;
  selected?: boolean;
  onClick: () => void;
}) {
  return (
    <button
      type="button"
      onClick={onClick}
      disabled={disabled}
      className={cn(
        "flex w-full items-center gap-3 rounded-2xl px-3 py-3 text-left transition-colors",
        selected ? "bg-transparent" : "hover:bg-muted/50",
        disabled && "cursor-not-allowed opacity-55",
      )}
    >
      <span className="flex size-11 shrink-0 items-center justify-center rounded-xl bg-muted/70">
        {icon}
      </span>
      <span className="min-w-0 flex-1">
        <span className="block truncate text-sm font-semibold">{title}</span>
        <span className="block text-xs leading-snug text-muted-foreground">{subtitle}</span>
      </span>
      <Badge variant={disabled ? "outline" : "available"} className="shrink-0">
        {badge}
      </Badge>
      <ChevronRight aria-hidden className="size-4 shrink-0 text-muted-foreground" />
    </button>
  );
}

function TrueMoneyResume({
  topup,
  onUpdated,
}: {
  topup: TopupDetail;
  onUpdated: (topup: TopupDetail, balanceSatang: number) => void;
}) {
  const [retrying, setRetrying] = useState(false);
  const [error, setError] = useState("");
  async function retry() {
    if (retrying) return;
    setRetrying(true);
    setError("");
    try {
      const result = await topupFetch<TopupCreateResponse>(
        `/api/topups/${topup.id}/retry`,
        { method: "POST" },
      );
      onUpdated(result.topup, result.balanceSatang);
    } catch (cause) {
      setError(clientErrorMessage(cause));
    } finally {
      setRetrying(false);
    }
  }
  const statusLabel =
    {
      pending: "รอชำระเงิน",
      processing: "กำลังตรวจสอบ",
      completed: "สำเร็จ",
      failed: "ไม่สำเร็จ",
      expired: "หมดอายุ",
    }[topup.status] ?? topup.status;
  return (
    <div className="flex flex-col gap-3">
      <Alert>
        <CircleAlert aria-hidden />
        <AlertTitle>รายการ TrueMoney</AlertTitle>
        <AlertDescription>
          สถานะ: {statusLabel}
          {topup.failureMessage ? ` · ${topup.failureMessage}` : ""}
          {topup.canRetry
            ? " · ระบบถูกเรียกถี่เกินไป สามารถลองอีกครั้งได้"
            : " · กรุณาตรวจสอบประวัติหรือติดต่อผู้ดูแลระบบ"}
        </AlertDescription>
      </Alert>
      {error ? (
        <Alert variant="destructive">
          <CircleAlert aria-hidden />
          <AlertDescription>{error}</AlertDescription>
        </Alert>
      ) : null}
      <div className="flex flex-wrap gap-2">
        {topup.canRetry ? (
          <Button onClick={() => void retry()} disabled={retrying}>
            {retrying ? (
              <Spinner data-icon="inline-start" />
            ) : (
              <RefreshCw data-icon="inline-start" />
            )}
            ลองอีกครั้ง
          </Button>
        ) : null}
        <Button variant="outline" nativeButton={false} render={<Link href="/history" />}>
          <History data-icon="inline-start" />
          ดูประวัติ
        </Button>
      </div>
    </div>
  );
}

/** Reads `?tx=` for resuming a pending transaction; rendered under Suspense. */
export function TopupPageWithSearch({ initialBalance }: { initialBalance: number }) {
  const searchParams = useSearchParams();
  return (
    <TopupPage
      initialBalance={initialBalance}
      initialTopupId={searchParams.get("tx") ?? undefined}
    />
  );
}
