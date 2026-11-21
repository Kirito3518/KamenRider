"use client";

import { useEffect, useRef, useState } from "react";
import Link from "next/link";
import { Monitor, MonitorPlay, MonitorX, Wallet } from "lucide-react";
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
        const data = (await leaseApi<Lease[]>("/api/my-leases")) ?? [];
        if (active) {
          if (
            leaseMutationsInFlight.current > 0 ||
            mutationVersionAtStart !== leaseMutationVersion.current
          )
            return;
          setNow(Date.now());
          setLeases(data);
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
  }, []);

  const beginLeaseMutation = () => {
    leaseMutationsInFlight.current += 1;
    leaseMutationVersion.current += 1;
  };
  const endLeaseMutation = () => {
    leaseMutationsInFlight.current -= 1;
    leaseMutationVersion.current += 1;
    if (leaseMutationsInFlight.current === 0) {
      refreshAfterMutation.current = true;
      void refreshLeasesRef.current?.();
    }
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
  const renderLeaseCard = (lease: Lease) =>
    lease.state === "preparing" ? (
      <PowerOnRentalCard key={lease.id} lease={lease} onRefresh={() => void refreshLeasesRef.current?.()} />
    ) : (
      <ActiveRentalCard
        lease={lease}
        balanceSatang={session?.user.balanceSatang ?? 0}
        now={now}
        extensionNotice={lease.id === selectedLease?.id ? notice : null}
        onRefresh={() => void refreshLeasesRef.current?.()}
        onExtend={(hours, idempotencyKey) => extendLease(lease, hours, idempotencyKey)}
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
              {formatBalance(session?.user.balanceSatang ?? 0)}
            </span>
          </div>
        </motion.header>

        <p
          role="status"
          className="mt-4 min-h-5 text-sm text-destructive"
        >
          {message}
        </p>

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
