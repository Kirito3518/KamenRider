"use client";

import { useCallback, useEffect, useState } from "react";
import Link from "next/link";
import { useSearchParams } from "next/navigation";
import {
  CircleAlert,
  Gift,
  History,
  Landmark,
  QrCode,
  RefreshCw,
  TicketPercent,
  Wallet,
} from "lucide-react";
import { motion, useReducedMotion } from "motion/react";
import { Alert, AlertDescription, AlertTitle } from "@/components/ui/alert";
import { Button } from "@/components/ui/button";
import { Card, CardContent, CardHeader, CardTitle } from "@/components/ui/card";
import { Skeleton } from "@/components/ui/skeleton";
import { Spinner } from "@/components/ui/spinner";
import { Tabs, TabsContent, TabsList, TabsTrigger } from "@/components/ui/tabs";
import { Footer } from "@/components/ui/layout/Footer";
import { Navbar } from "@/components/ui/layout/Navbar";
import { formatBalance } from "@/lib/money";
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
import {
  clientErrorMessage,
  topupFetch,
} from "@/components/topup/client";

type ChannelTab = "truemoney" | "bank" | "coupon";

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
  const [tab, setTab] = useState<ChannelTab>("truemoney");
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
        setTab(data.topup.channel === "coupon" ? "coupon" : data.topup.channel === "truemoney" ? "truemoney" : "bank");
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

  const bankLabel =
    config?.bankRail === "promptpay_qr"
      ? "PromptPay QR"
      : config?.bankRail === "bank_transfer"
        ? "โอนผ่านธนาคาร"
        : "ชำระผ่านธนาคาร";
  const BankIcon = config?.bankRail === "bank_transfer" ? Landmark : QrCode;

  return (
    <div className="flex min-h-screen flex-col">
      <Navbar />
      <main className="mx-auto w-full max-w-6xl flex-1 px-5 pt-28 pb-16 sm:pt-32">
        <motion.header
          initial={reduceMotion ? false : { opacity: 0, y: 10 }}
          animate={{ opacity: 1, y: 0 }}
          transition={reduceMotion ? { duration: 0 } : { duration: 0.28 }}
          className="flex flex-col justify-between gap-4 sm:flex-row sm:items-end"
        >
          <div>
            <h1 className="flex items-center gap-2 text-3xl font-bold">
              <Wallet aria-hidden className="size-7 text-primary" />
              เติมเงิน
            </h1>
            <p className="mt-1 text-sm text-muted-foreground">
              เลือกช่องทางที่สะดวก ระบบตรวจสอบยอดและบัญชีผู้รับให้อัตโนมัติ
            </p>
          </div>
          <div className="flex flex-wrap items-center gap-2">
            <div className="flex items-center gap-2 rounded-xl border bg-card px-3 py-2 text-sm">
              <Wallet aria-hidden className="size-4 text-muted-foreground" />
              <span className="text-muted-foreground">ยอดเงินคงเหลือ</span>
              <span className="font-semibold tabular-nums" aria-live="polite">
                {formatBalance(balanceSatang)}
              </span>
            </div>
            <Button variant="outline" size="sm" nativeButton={false} render={<Link href="/history" />}>
              <History data-icon="inline-start" />
              ประวัติ
            </Button>
            <Button
              variant="outline"
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
          <Card className="mt-6 gap-4 rounded-2xl border shadow-none">
            <CardHeader>
              <CardTitle className="flex items-center gap-2">
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
          <div className="mt-6 grid gap-4 lg:grid-cols-[minmax(0,1fr)_minmax(0,420px)]">
            <Skeleton className="h-72 w-full" />
            <Skeleton className="h-72 w-full" />
          </div>
        ) : config ? (
          <motion.div
            initial={reduceMotion ? false : { opacity: 0, y: 10 }}
            animate={{ opacity: 1, y: 0 }}
            transition={reduceMotion ? { duration: 0 } : { duration: 0.28, delay: 0.05 }}
            className="mt-6"
          >
            <Tabs
              value={tab}
              onValueChange={(value) => setTab(value as ChannelTab)}
              className="gap-5"
            >
              <TabsList aria-label="ช่องทางการเติมเงิน" className="h-auto w-full flex-wrap sm:w-fit">
                <TabsTrigger value="truemoney">
                  <Gift aria-hidden />
                  TrueMoney
                </TabsTrigger>
                <TabsTrigger value="bank">
                  <BankIcon aria-hidden />
                  {bankLabel}
                </TabsTrigger>
                <TabsTrigger value="coupon">
                  <TicketPercent aria-hidden />
                  คูปอง
                </TabsTrigger>
              </TabsList>
              <TabsContent value="truemoney" keepMounted>
                <TrueMoneyForm config={config} onBalance={setBalanceSatang} />
              </TabsContent>
              <TabsContent value="bank" keepMounted>
                {config.bankRail === "promptpay_qr" ? (
                  <PromptpayForm config={config} onBalance={setBalanceSatang} />
                ) : config.bankRail === "bank_transfer" ? (
                  <BankTransferForm config={config} onBalance={setBalanceSatang} />
                ) : (
                  <Alert>
                    <CircleAlert aria-hidden />
                    <AlertTitle>ยังไม่เปิดช่องทางธนาคาร</AlertTitle>
                    <AlertDescription>
                      ผู้ดูแลระบบยังไม่ได้เปิด PromptPay QR หรือโอนผ่านบัญชีธนาคาร
                    </AlertDescription>
                  </Alert>
                )}
              </TabsContent>
              <TabsContent value="coupon" keepMounted>
                <CouponForm config={config} />
              </TabsContent>
            </Tabs>

            <div className="mt-6 grid gap-3 text-xs text-muted-foreground sm:grid-cols-3">
              <div className="rounded-2xl border bg-card p-4">
                <p className="font-medium text-foreground">ตรวจสอบอัตโนมัติ</p>
                <p className="mt-1">ระบบตรวจยอดและบัญชีผู้รับก่อนเพิ่มเงินทุกครั้ง</p>
              </div>
              <div className="rounded-2xl border bg-card p-4">
                <p className="font-medium text-foreground">ปลอดภัย</p>
                <p className="mt-1">เพิ่มยอดเงินเพียงครั้งเดียวและไม่เชื่อยอดจากเบราว์เซอร์</p>
              </div>
              <div className="rounded-2xl border bg-card p-4">
                <p className="font-medium text-foreground">มีประวัติ</p>
                <p className="mt-1">ตรวจสอบรายการย้อนหลังได้ที่หน้าประวัติการเติมเงิน</p>
              </div>
            </div>
          </motion.div>
        ) : null}
      </main>
      <Footer />
    </div>
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
