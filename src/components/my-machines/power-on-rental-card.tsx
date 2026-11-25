"use client";

import { useState } from "react";
import { LoaderCircle, Power, RotateCcw, XCircle } from "lucide-react";
import { AnimatePresence, motion, useReducedMotion } from "motion/react";
import {
  AlertDialog,
  AlertDialogAction,
  AlertDialogCancel,
  AlertDialogContent,
  AlertDialogDescription,
  AlertDialogFooter,
  AlertDialogHeader,
  AlertDialogTitle,
} from "@/components/ui/alert-dialog";
import { Alert, AlertDescription, AlertTitle } from "@/components/ui/alert";
import { Button } from "@/components/ui/button";
import {
  Card,
  CardContent,
  CardDescription,
  CardFooter,
  CardHeader,
  CardTitle,
} from "@/components/ui/card";
import { formatBalance } from "@/lib/money";
import type { Lease } from "@/lib/machine-contracts";
import {
  cancelRental,
  cancelRentalMessage,
  leaseErrorMessage,
  requestRentalPower,
} from "@/components/my-machines/lease-api";

/**
 * The renter's prepared lease. The card deliberately keeps the same outer
 * footprint as [[ActiveRentalCard]] and exposes only one green Power action;
 * after the press it becomes a waiting animation until the Agent boots.
 */
export function PowerOnRentalCard({
  lease,
  onRefresh,
  onCancelled,
  onMutationStart,
  onMutationEnd,
}: {
  lease: Lease;
  onRefresh: () => void;
  /** Receives the authoritative post-refund balance returned by the cancel route. */
  onCancelled?: (balanceSatang?: number) => void;
  /** Optional hooks so the page can suppress poll-driven refund notices mid-request. */
  onMutationStart?: () => void;
  onMutationEnd?: () => void;
}) {
  const reduceMotion = useReducedMotion();
  const [powering, setPowering] = useState(false);
  const [cancelling, setCancelling] = useState(false);
  const [cancelOpen, setCancelOpen] = useState(false);
  const [error, setError] = useState("");
  const [refundNotice, setRefundNotice] = useState("");
  const waiting = Boolean(lease.wakeRequestedAtUtc);
  const name = lease.machineDisplayName || lease.machineId;
  const paid = lease.payment?.paidSatang ?? 0;

  async function powerOn() {
    if (powering) return;
    setPowering(true);
    setError("");
    onMutationStart?.();
    try {
      await requestRentalPower(lease.id);
    } catch (cause) {
      setError(leaseErrorMessage(cause));
    } finally {
      setPowering(false);
      onMutationEnd?.();
    }
    onRefresh();
  }

  async function cancel() {
    if (cancelling) return;
    setCancelling(true);
    setError("");
    onMutationStart?.();
    try {
      const result = await cancelRental(lease.id, lease.revision);
      setCancelOpen(false);
      setRefundNotice(cancelRentalMessage(result));
      onCancelled?.(result.balanceSatang);
      onRefresh();
    } catch (cause) {
      setError(leaseErrorMessage(cause));
    } finally {
      setCancelling(false);
      onMutationEnd?.();
    }
  }

  return (
    <motion.section
      initial={reduceMotion ? false : { opacity: 0, y: 12 }}
      animate={{ opacity: 1, y: 0 }}
      transition={
        reduceMotion ? { duration: 0 } : { duration: 0.24, ease: [0.22, 1, 0.36, 1] }
      }
    >
      <Card size="sm" className="min-h-[360px] rounded-3xl border-2 border-primary/25 shadow-none">
        <CardHeader className="bg-gradient-to-b from-primary/[0.07] to-transparent">
          <div className="flex items-center gap-3">
            <span className="grid size-11 shrink-0 place-items-center rounded-2xl bg-primary/10 text-primary">
              <Power aria-hidden className="size-5" />
            </span>
            <div className="min-w-0 flex-1">
              <CardTitle>
                <h2 className="truncate text-xl font-bold tracking-tight">{name}</h2>
              </CardTitle>
              <CardDescription>
                {waiting ? "กำลังเปิดเครื่องและรอเครื่องพร้อมใช้งาน" : "กดปุ่มเพื่อเปิดเครื่องนี้"}
              </CardDescription>
            </div>
          </div>
        </CardHeader>

        <CardContent className="flex flex-1 flex-col items-center justify-center gap-5">
          <AnimatePresence mode="wait" initial={false}>
            {waiting ? (
              <motion.div
                key="waiting"
                initial={reduceMotion ? false : { opacity: 0, scale: 0.9 }}
                animate={{ opacity: 1, scale: 1 }}
                exit={reduceMotion ? undefined : { opacity: 0, scale: 0.95 }}
                className="relative grid size-36 place-items-center"
              >
                <motion.span
                  aria-hidden
                  className="absolute inset-0 rounded-full border-2 border-primary/30"
                  animate={reduceMotion ? undefined : { scale: [0.88, 1.08, 0.88], opacity: [0.25, 0.75, 0.25] }}
                  transition={{ duration: 1.8, repeat: Infinity, ease: "easeInOut" }}
                />
                <motion.span
                  aria-hidden
                  className="absolute inset-3 rounded-full border border-primary/50"
                  animate={reduceMotion ? undefined : { rotate: 360 }}
                  transition={{ duration: 2.4, repeat: Infinity, ease: "linear" }}
                >
                  <span className="absolute left-1/2 top-0 size-2 -translate-x-1/2 -translate-y-1/2 rounded-full bg-primary" />
                </motion.span>
                <LoaderCircle aria-hidden className="size-10 animate-spin text-primary" />
                <span className="sr-only" role="status">กำลังเปิดเครื่อง รอเครื่องออนไลน์</span>
              </motion.div>
            ) : (
              <motion.button
                key="power"
                type="button"
                onClick={() => void powerOn()}
                disabled={powering}
                initial={reduceMotion ? false : { opacity: 0, scale: 0.92 }}
                animate={{ opacity: 1, scale: 1 }}
                exit={reduceMotion ? undefined : { opacity: 0, scale: 0.96 }}
                whileHover={reduceMotion ? undefined : { scale: 1.04 }}
                whileTap={reduceMotion ? undefined : { scale: 0.97 }}
                className="group grid size-36 place-items-center rounded-full bg-status-available text-white shadow-[0_18px_50px_-20px_var(--color-status-available)] outline-none transition-colors hover:bg-status-available/90 focus-visible:ring-4 focus-visible:ring-status-available/40 disabled:opacity-60"
                aria-label={`เปิดเครื่อง ${name}`}
              >
                {powering ? (
                  <LoaderCircle aria-hidden className="size-14 animate-spin" />
                ) : (
                  <Power aria-hidden className="size-16" strokeWidth={1.6} />
                )}
                <span className="sr-only">{powering ? "กำลังส่งคำสั่งเปิดเครื่อง" : "เปิดเครื่อง"}</span>
              </motion.button>
            )}
          </AnimatePresence>

          <p role="status" className="text-center text-sm text-muted-foreground">
            {waiting
              ? "เครื่องกำลังบูต หน้านี้จะอัปเดตเมื่อพร้อมเชื่อมต่อ"
              : "หลังกดเปิดเครื่อง ระบบจะส่งสัญญาณ Wake-on-LAN ให้เครื่อง"}
          </p>

          {error ? (
            <Alert variant="destructive" className="w-full">
              <AlertTitle>เปิดเครื่องไม่สำเร็จ</AlertTitle>
              <AlertDescription className="flex flex-wrap items-center gap-2">
                {error}
                {!waiting ? (
                  <Button variant="outline" size="sm" onClick={() => void powerOn()} disabled={powering}>
                    <RotateCcw data-icon="inline-start" />
                    ลองใหม่
                  </Button>
                ) : null}
              </AlertDescription>
            </Alert>
          ) : null}
          {refundNotice ? <p role="status" className="text-sm text-status-available">{refundNotice}</p> : null}
        </CardContent>

        <CardFooter className="flex flex-col gap-3 sm:flex-row sm:items-center sm:justify-between">
          <p className="text-sm text-muted-foreground">
            ชำระแล้ว {formatBalance(paid)} · กดยกเลิกเพื่อคืนเงินตามเวลาที่เหลือ
          </p>
          <Button variant="destructive" size="sm" onClick={() => setCancelOpen(true)} disabled={cancelling}>
            <XCircle data-icon="inline-start" />
            ยกเลิกการเช่า
          </Button>
        </CardFooter>
      </Card>

      <AlertDialog open={cancelOpen} onOpenChange={(next) => { if (!cancelling) setCancelOpen(next); }}>
        <AlertDialogContent size="sm">
          <AlertDialogHeader>
            <AlertDialogTitle>ยกเลิกการเช่า {name}?</AlertDialogTitle>
            <AlertDialogDescription>
              ระบบจะสั่งยกเลิก จับคู่ Sunshine ออก และปิดเครื่อง จากนั้นคืนเงินตามเวลาที่เหลือ
            </AlertDialogDescription>
          </AlertDialogHeader>
          <AlertDialogFooter>
            <AlertDialogCancel disabled={cancelling}>ไม่ใช่ตอนนี้</AlertDialogCancel>
            <AlertDialogAction variant="destructive" onClick={() => void cancel()} disabled={cancelling}>
              {cancelling ? <LoaderCircle aria-hidden className="animate-spin" /> : <XCircle aria-hidden />}
              ยืนยันยกเลิก
            </AlertDialogAction>
          </AlertDialogFooter>
        </AlertDialogContent>
      </AlertDialog>
    </motion.section>
  );
}
