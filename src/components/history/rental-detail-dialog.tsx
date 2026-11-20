"use client";

import { Badge } from "@/components/ui/badge";
import {
  Dialog,
  DialogContent,
  DialogDescription,
  DialogHeader,
  DialogTitle,
} from "@/components/ui/dialog";
import { formatBalance } from "@/lib/money";
import {
  formatRentalDateTime,
  formatRentalDuration,
  rentalDisplayState,
  rentalDisplayStateLabel,
  rentalDisplayStateTone,
  type RentalHistoryEntry,
} from "@/lib/rental-history";

export function RentalDetailDialog({
  entry,
  open,
  onOpenChange,
  user,
}: {
  entry: RentalHistoryEntry | null;
  open: boolean;
  onOpenChange: (open: boolean) => void;
  user?: { userName: string; userEmail: string };
}) {
  if (!entry) return null;
  const state = rentalDisplayState(entry);
  const endTime = entry.endedAtUtc
    ? formatRentalDateTime(entry.endedAtUtc)
    : entry.expiresAtUtc
      ? `กำหนด ${formatRentalDateTime(entry.expiresAtUtc)}`
      : "—";
  return (
    <Dialog open={open} onOpenChange={onOpenChange}>
      <DialogContent className="max-h-[90vh] w-[calc(100%-2rem)] max-w-xl overflow-y-auto">
        <DialogHeader>
          <DialogTitle>{entry.machineName}</DialogTitle>
          <DialogDescription>รหัสรายการ {entry.rentalId}</DialogDescription>
        </DialogHeader>
        <div className="flex flex-col gap-4 text-sm">
          <div className="flex flex-wrap items-center gap-2">
            <Badge variant={rentalDisplayStateTone(state)}>
              {rentalDisplayStateLabel(state)}
            </Badge>
            <span className="text-xs text-muted-foreground">
              สถานะจากระบบเครื่อง: {entry.state}
            </span>
          </div>
          <dl className="grid gap-3 rounded-2xl border bg-card p-4 sm:grid-cols-2">
            {user ? (
              <div className="sm:col-span-2">
                <dt className="text-muted-foreground">ผู้เช่า</dt>
                <dd>
                  {user.userName}
                  {user.userEmail ? ` · ${user.userEmail}` : ""}
                </dd>
              </div>
            ) : null}
            <div>
              <dt className="text-muted-foreground">เวลาเริ่ม</dt>
              <dd>{formatRentalDateTime(entry.startsAtUtc)}</dd>
            </div>
            <div>
              <dt className="text-muted-foreground">เวลาสิ้นสุด</dt>
              <dd>{endTime}</dd>
            </div>
            <div>
              <dt className="text-muted-foreground">ระยะเวลาที่ชำระ</dt>
              <dd>{formatRentalDuration(entry.bookedSeconds + entry.extensionSeconds)}</dd>
            </div>
            <div>
              <dt className="text-muted-foreground">ราคา/ชั่วโมง</dt>
              <dd>
                {entry.hourlyPriceSatang === null
                  ? "—"
                  : formatBalance(entry.hourlyPriceSatang)}
              </dd>
            </div>
            <div>
              <dt className="text-muted-foreground">ชำระ</dt>
              <dd>
                {entry.paidSatang === null ? "—" : formatBalance(entry.paidSatang)}
              </dd>
            </div>
            <div>
              <dt className="text-muted-foreground">คืนเงิน</dt>
              <dd>{formatBalance(entry.refundedSatang)}</dd>
            </div>
            <div>
              <dt className="text-muted-foreground">สุทธิ</dt>
              <dd className="font-semibold">
                {entry.netSatang === null ? "—" : formatBalance(entry.netSatang)}
              </dd>
            </div>
            <div>
              <dt className="text-muted-foreground">วันที่ทำรายการ</dt>
              <dd>{formatRentalDateTime(entry.createdAtUtc)}</dd>
            </div>
          </dl>
          {entry.refundNote ? (
            <div className="rounded-xl border bg-card p-3">
              <p className="text-xs text-muted-foreground">หมายเหตุการคืนเงิน</p>
              <p>{entry.refundNote}</p>
            </div>
          ) : null}
          {entry.rejectReason ? (
            <div className="rounded-xl border bg-card p-3">
              <p className="text-xs text-muted-foreground">เหตุผลที่ไม่สำเร็จ</p>
              <p>{entry.rejectReason}</p>
            </div>
          ) : null}
        </div>
      </DialogContent>
    </Dialog>
  );
}
