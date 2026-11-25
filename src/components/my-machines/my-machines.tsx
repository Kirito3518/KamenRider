"use client";

import { useCallback, useEffect, useRef, useState } from "react";
import Link from "next/link";
import {
  CircleCheck,
  Monitor,
  MonitorPlay,
  MonitorX,
  Wallet,
} from "lucide-react";
import { AnimatePresence, motion, useReducedMotion } from "motion/react";
import { Button } from "@/components/ui/button";
import {
  Empty,
  EmptyContent,
  EmptyDescription,
  EmptyHeader,
  EmptyMedia,
  EmptyTitle,
} from "@/components/ui/empty";
import { Footer } from "@/components/ui/layout/Footer";
import { Navbar } from "@/components/ui/layout/Navbar";
import { Spinner } from "@/components/ui/spinner";
import { Tabs, TabsContent, TabsList, TabsTrigger } from "@/components/ui/tabs";
import { authClient } from "@/lib/auth-client";
import { formatBalance } from "@/lib/money";
import type { Lease } from "@/lib/machine-contracts";
import { ActiveRentalCard } from "@/components/my-machines/active-rental-card";
import { PowerOnRentalCard } from "@/components/my-machines/power-on-rental-card";
import { leaseApi, leaseErrorMessage } from "@/components/my-machines/lease-api";
import { CardsGridSkeleton, LoadingRegion } from "@/components/loading";

const leaseRefreshIntervalMs = 3_000;
/** How long the "unexpected refund" banner stays before fading away. */
const refundNoticeDurationMs = 10_000;

/** The lease feed settles refunds before responding and returns the wallet too. */
type MyLeasesResponse = {
  leases: Lease[];
  balanceSatang: number;
};

function mergeExtendedLease(current: Lease, updated: Lease): Lease {
  // The extend endpoint returns the raw Controller lease; keep the enriched
  // connection/parsec/payment fields that were fetched by the list endpoint.
  return {
    ...current,
    state: updated.state ?? current.state,
    revision:
      typeof updated.revision === "number"
        ? updated.revision
        : current.revision,
    pendingRevision: updated.pendingRevision ?? null,
    startsAtUtc: updated.startsAtUtc ?? current.startsAtUtc,
    expiresAtUtc: updated.expiresAtUtc ?? current.expiresAtUtc,
    endedAtUtc: updated.endedAtUtc ?? current.endedAtUtc,
  };
}

function leaseName(lease: Lease) {
  return lease.machineDisplayName || lease.machineId;
}

export function MyMachines() {
  const { data: session, refetch: refetchSession } = authClient.useSession();
  const reduceMotion = useReducedMotion();
  const [leases, setLeases] = useState<Lease[]>([]);
  // Locally tracked wallet. Seeded from the session, then kept current from every
  // lease poll and from mutation responses so refunds appear immediately without
  // waiting for a session refetch.
  const [balanceSatang, setBalanceSatang] = useState<number | null>(null);
  // Amount of an unexpected (poll-driven) refund banner; null when hidden.
  const [refundNoticeSatang, setRefundNoticeSatang] = useState<number | null>(
    null,
  );
  const [message, setMessage] = useState("");
  const [loading, setLoading] = useState(true);
  const [now, setNow] = useState(0);
  const [selectedId, setSelectedId] = useState<string | null>(null);
  const [extensionNotice, setExtensionNotice] = useState<{
    leaseId: string;
    text: string;
  } | null>(null);
  const refreshLeasesRef = useRef<(() => Promise<void>) | null>(null);
  const leaseMutationVersion = useRef(0);
  const leaseMutationsInFlight = useRef(0);
  const refreshAfterMutation = useRef(false);
  const balanceRef = useRef<number | null>(null);
  const refundNoticeTimer = useRef<number | null>(null);

  const applyBalance = useCallback((next: number) => {
    if (!Number.isFinite(next)) return;
    balanceRef.current = next;
    setBalanceSatang(next);
  }, []);

  const dismissRefundNotice = useCallback(() => {
    if (refundNoticeTimer.current !== null) {
      window.clearTimeout(refundNoticeTimer.current);
      refundNoticeTimer.current = null;
    }
    setRefundNoticeSatang(null);
  }, []);

  /** Announce a poll-discovered credit that no local mutation produced. */
  const announceRefund = useCallback((amountSatang: number) => {
    if (!(amountSatang > 0)) return;
    if (refundNoticeTimer.current !== null)
      window.clearTimeout(refundNoticeTimer.current);
    setRefundNoticeSatang(amountSatang);
    refundNoticeTimer.current = window.setTimeout(() => {
      refundNoticeTimer.current = null;
      setRefundNoticeSatang(null);
    }, refundNoticeDurationMs);
  }, []);

  const sessionBalanceSatang = session?.user.balanceSatang;
  // Mirror the latest session wallet into a ref so the polling loop can compare
  // against it without re-subscribing or writing state from an effect.
  const sessionBalanceRef = useRef<number | null>(null);

  useEffect(() => {
    sessionBalanceRef.current =
      typeof sessionBalanceSatang === "number" ? sessionBalanceSatang : null;
  }, [sessionBalanceSatang]);

  useEffect(() => {
    return () => {
      if (refundNoticeTimer.current !== null)
        window.clearTimeout(refundNoticeTimer.current);
    };
  }, []);

  useEffect(() => {
    let active = true;
    let inFlight = false;
    const refresh = async () => {
      if (!active || document.visibilityState !== "visible" || inFlight) return;
      inFlight = true;
      if (leaseMutationsInFlight.current === 0)
        refreshAfterMutation.current = false;
      const mutationVersionAtStart = leaseMutationVersion.current;
      try {
        const data = await leaseApi<MyLeasesResponse>("/api/my-leases");
        if (active) {
          if (
            leaseMutationsInFlight.current > 0 ||
            mutationVersionAtStart !== leaseMutationVersion.current
          )
            return;
          const nextBalanceSatang =
            typeof data?.balanceSatang === "number" ? data.balanceSatang : null;
          const previousBalanceSatang =
            balanceRef.current ?? sessionBalanceRef.current;
          if (nextBalanceSatang !== null) {
            applyBalance(nextBalanceSatang);
            // The route settles unused-time refunds before answering; a credit
            // that no local mutation accounted for is exactly such a refund.
            if (
              previousBalanceSatang !== null &&
              nextBalanceSatang > previousBalanceSatang
            )
              announceRefund(nextBalanceSatang - previousBalanceSatang);
          }
          setNow(Date.now());
          setLeases(Array.isArray(data?.leases) ? data.leases : []);
          setMessage("");
          setLoading(false);
        }
      } catch (error) {
        if (
          active &&
          leaseMutationsInFlight.current === 0 &&
          mutationVersionAtStart === leaseMutationVersion.current
        ) {
          setMessage(leaseErrorMessage(error));
          setLoading(false);
        }
      } finally {
        inFlight = false;
        if (
          active &&
          document.visibilityState === "visible" &&
          leaseMutationsInFlight.current === 0 &&
          refreshAfterMutation.current
        ) {
          refreshAfterMutation.current = false;
          void refresh();
        }
      }
    };
    refreshLeasesRef.current = refresh;
    void refresh();
    const intervalId = window.setInterval(
      () => void refresh(),
      leaseRefreshIntervalMs,
    );
    document.addEventListener("visibilitychange", refresh);
    return () => {
      active = false;
      window.clearInterval(intervalId);
      document.removeEventListener("visibilitychange", refresh);
      if (refreshLeasesRef.current === refresh) refreshLeasesRef.current = null;
    };
  }, [announceRefund, applyBalance]);

  const beginLeaseMutation = () => {
    leaseMutationsInFlight.current += 1;
    leaseMutationVersion.current += 1;
    // A refund banner describes an earlier poll, not the action just started.
    dismissRefundNotice();
  };
  const endLeaseMutation = () => {
    leaseMutationsInFlight.current -= 1;
    leaseMutationVersion.current += 1;
    if (leaseMutationsInFlight.current === 0) {
      refreshAfterMutation.current = true;
      void refreshLeasesRef.current?.();
    }
  };

  const handleLeaseCancelled = (nextBalanceSatang?: number) => {
    if (typeof nextBalanceSatang === "number") applyBalance(nextBalanceSatang);
    void refreshLeasesRef.current?.();
  };

  async function extendLease(
    lease: Lease,
    hours: number,
    idempotencyKey: string,
  ) {
    beginLeaseMutation();
    try {
      const updated = await leaseApi<Lease>(
        `/api/my-leases/${encodeURIComponent(lease.id)}/extend`,
        {
          additionalSeconds: hours * 3_600,
          expectedRevision: lease.revision,
          idempotencyKey,
        },
      );
      setLeases((current) =>
        current.map((item) =>
          item.id === lease.id ? mergeExtendedLease(item, updated) : item,
        ),
      );
      setExtensionNotice({
        leaseId: lease.id,
        text: "ต่อเวลาแล้ว รอเครื่องยืนยันการเปลี่ยนแปลง",
      });
      void refetchSession();
    } finally {
      endLeaseMutation();
    }
  }

  const visibleLeases = leases.filter(
    (lease) => lease.state === "active" || lease.state === "preparing",
  );
  const pendingPayments = leases.filter((lease) => lease.state === "pending").length;
  const selectedLease =
    visibleLeases.find((lease) => lease.id === selectedId) ??
    visibleLeases[0] ??
    null;
  const nameCounts = new Map<string, number>();
  for (const lease of visibleLeases) {
    const name = leaseName(lease);
    nameCounts.set(name, (nameCounts.get(name) ?? 0) + 1);
  }
  const tabLabel = (lease: Lease) => {
    const name = leaseName(lease);
    return (nameCounts.get(name) ?? 0) > 1
      ? `${name} · ${lease.id.slice(0, 6)}`
      : name;
  };
  const notice =
    extensionNotice && selectedLease?.id === extensionNotice.leaseId
      ? extensionNotice.text
      : null;
  const displayedBalanceSatang = balanceSatang ?? sessionBalanceSatang ?? 0;
  const renderLeaseCard = (lease: Lease) =>
    lease.state === "preparing" ? (
      <PowerOnRentalCard
        key={lease.id}
        lease={lease}
        onRefresh={() => void refreshLeasesRef.current?.()}
        onCancelled={handleLeaseCancelled}
        onMutationStart={beginLeaseMutation}
        onMutationEnd={endLeaseMutation}
      />
    ) : (
      <ActiveRentalCard
        lease={lease}
        balanceSatang={displayedBalanceSatang}
        now={now}
        extensionNotice={lease.id === selectedLease?.id ? notice : null}
        onRefresh={() => void refreshLeasesRef.current?.()}
        onExtend={(hours, idempotencyKey) => extendLease(lease, hours, idempotencyKey)}
        onCancelled={handleLeaseCancelled}
        onMutationStart={beginLeaseMutation}
        onMutationEnd={endLeaseMutation}
      />
    );

  return (
    <div className="flex min-h-screen flex-col">
      <Navbar />
      <main className="mx-auto w-full max-w-5xl flex-1 px-5 pt-8 pb-16 sm:pt-10">
        <motion.header
          initial={reduceMotion ? false : { opacity: 0, y: 10 }}
          animate={{ opacity: 1, y: 0 }}
          transition={
            reduceMotion
              ? { duration: 0 }
              : { duration: 0.28, ease: [0.22, 1, 0.36, 1] }
          }
          className="flex flex-col justify-between gap-4 sm:flex-row sm:items-end"
        >
          <div>
            <h1 className="flex items-center gap-2 text-3xl font-bold tracking-tight">
              <MonitorPlay aria-hidden className="size-7 text-primary" />
              เครื่องของฉัน
            </h1>
            <p className="mt-1 text-sm text-muted-foreground">
              แสดงเฉพาะเครื่องที่คุณกำลังเช่าอยู่
            </p>
          </div>
          <div className="flex w-fit items-center gap-2 rounded-xl border bg-card px-3 py-2 text-sm">
            <Wallet
              aria-hidden
              className="size-4 text-muted-foreground"
            />
            <span className="text-muted-foreground">ยอดเงินคงเหลือ</span>
            <span className="font-semibold tabular-nums" aria-live="polite">
              {formatBalance(displayedBalanceSatang)}
            </span>
          </div>
        </motion.header>

        <p
          role="status"
          className="mt-4 min-h-5 text-sm text-destructive"
        >
          {message}
        </p>

        {refundNoticeSatang !== null ? (
          <p
            role="status"
            className="mt-1 flex items-start gap-2 rounded-xl border border-status-available/30 bg-status-available/10 px-3 py-2 text-sm text-status-available"
          >
            <CircleCheck aria-hidden className="mt-0.5 size-4 shrink-0" />
            <span>
              คืนเงินค่าเช่าที่ไม่ได้ใช้ {formatBalance(refundNoticeSatang)}{" "}
              เข้ายอดเงินแล้ว
            </span>
          </p>
        ) : null}

        {loading ? (
          <LoadingRegion label="กำลังโหลดเครื่องของฉัน" className="mt-2">
            <CardsGridSkeleton count={1} rows={1} columns="grid-cols-1" cardClassName="h-[360px]" />
          </LoadingRegion>
        ) : null}

        {!loading && pendingPayments > 0 ? (
          <div
            role="status"
            className="mb-4 flex items-center gap-3 rounded-2xl border border-dashed bg-muted/30 p-4 text-sm"
          >
            <Spinner />
            <div className="flex flex-col gap-0.5">
              <p className="font-medium">
                กำลังตรวจสอบการชำระเงิน {pendingPayments} รายการ
              </p>
              <p className="text-muted-foreground">
                เมื่อชำระเงินสำเร็จ เครื่องจะปรากฏเป็นการ์ดเปิดเครื่องที่นี่
              </p>
            </div>
          </div>
        ) : null}

        {!loading && !message && visibleLeases.length === 0 && pendingPayments === 0 ? (
          <Empty className="mt-6 border">
            <EmptyHeader>
              <EmptyMedia variant="icon">
                <MonitorX aria-hidden />
              </EmptyMedia>
              <EmptyTitle>ยังไม่มีเครื่องที่กำลังเช่า</EmptyTitle>
              <EmptyDescription>
                เลือกเครื่องจากหน้าเช่า แล้วเครื่องที่กำลังใช้งานจะแสดงที่นี่
              </EmptyDescription>
            </EmptyHeader>
            <EmptyContent>
              <Button nativeButton={false} render={<Link href="/rent" />}>
                เลือกเครื่องที่หน้าเช่า
              </Button>
              <Button
                variant="outline"
                nativeButton={false}
                render={<Link href="/history" />}
              >
                ประวัติการเช่า
              </Button>
            </EmptyContent>
          </Empty>
        ) : null}

        {!loading && visibleLeases.length > 1 && selectedLease ? (
          <Tabs
            value={selectedLease.id}
            onValueChange={(value) => setSelectedId(String(value))}
            className="mt-2 gap-4"
          >
            <TabsList
              aria-label="เลือกเครื่องที่กำลังเช่า"
              className="h-auto w-full flex-wrap sm:w-fit"
            >
              {visibleLeases.map((lease) => (
                <TabsTrigger key={lease.id} value={lease.id}>
                  <Monitor aria-hidden />
                  {tabLabel(lease)}
                </TabsTrigger>
              ))}
            </TabsList>
            <TabsContent value={selectedLease.id}>
              <AnimatePresence mode="wait" initial={false}>
                <motion.div
                  key={selectedLease.id}
                  initial={reduceMotion ? false : { opacity: 0, y: 8 }}
                  animate={{ opacity: 1, y: 0 }}
                  exit={reduceMotion ? undefined : { opacity: 0, y: -8 }}
                  transition={{ duration: 0.18 }}
                >
                  {renderLeaseCard(selectedLease)}
                </motion.div>
              </AnimatePresence>
            </TabsContent>
          </Tabs>
        ) : !loading && selectedLease ? (
          <div className="mt-2">{renderLeaseCard(selectedLease)}</div>
        ) : null}
      </main>
      <Footer />
    </div>
  );
}
