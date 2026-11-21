"use client";

import { useState } from "react";
import { LoaderCircle, XCircle } from "lucide-react";
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
import { Button } from "@/components/ui/button";
import { formatBalance } from "@/lib/money";
import type { Lease } from "@/lib/machine-contracts";
import { cancelRental, leaseErrorMessage } from "@/components/my-machines/lease-api";

export function CancelRentalButton({
  lease,
  onCancelled,
  className,
}: {
  lease: Lease;
  onCancelled: () => void;
  className?: string;
}) {
  const [open, setOpen] = useState(false);
  const [busy, setBusy] = useState(false);
  const [message, setMessage] = useState("");
  const [error, setError] = useState("");
  const name = lease.machineDisplayName || lease.machineId;

  async function submit() {
    if (busy) return;
    setBusy(true);
    setError("");
    try {
      const result = await cancelRental(lease.id, lease.revision);
      setOpen(false);
      setMessage(
        result.refundedSatang > 0
          ? `ยกเลิกแล้ว คืนเงิน ${formatBalance(result.refundedSatang)}`
          : "ยกเลิกการเช่าแล้ว",
      );
      onCancelled();
    } catch (cause) {
      setError(leaseErrorMessage(cause));
    } finally {
      setBusy(false);
    }
  }

  return (
    <span className={className}>
      <Button variant="outline" size="sm" onClick={() => setOpen(true)} disabled={busy}>
        <XCircle data-icon="inline-start" />
        ยกเลิกการเช่า
      </Button>
      {message ? <p role="status" className="mt-1 text-xs text-status-available">{message}</p> : null}
      {error ? <p role="alert" className="mt-1 text-xs text-destructive">{error}</p> : null}
      <AlertDialog open={open} onOpenChange={(next) => { if (!busy) setOpen(next); }}>
        <AlertDialogContent size="sm">
          <AlertDialogHeader>
            <AlertDialogTitle>ยกเลิกการเช่า {name}?</AlertDialogTitle>
            <AlertDialogDescription>
              ระบบจะสั่งยกเลิก จับคู่ Sunshine ออก และปิดเครื่อง จากนั้นคืนเงินตามเวลาที่เหลือ
            </AlertDialogDescription>
          </AlertDialogHeader>
          <AlertDialogFooter>
            <AlertDialogCancel disabled={busy}>ไม่ใช่ตอนนี้</AlertDialogCancel>
            <AlertDialogAction variant="destructive" onClick={() => void submit()} disabled={busy}>
              {busy ? <LoaderCircle aria-hidden className="animate-spin" /> : <XCircle aria-hidden />}
              ยืนยันยกเลิก
            </AlertDialogAction>
          </AlertDialogFooter>
        </AlertDialogContent>
      </AlertDialog>
    </span>
  );
}
