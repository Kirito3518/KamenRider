"use client";

import { useRef, useState, type MouseEvent, type ReactNode } from "react";
import Link from "next/link";
import {
  AlertTriangle,
  Clock,
  History,
  Monitor,
  Plus,
  Radio,
  RefreshCw,
  Tag,
  Wallet,
} from "lucide-react";
import { AnimatePresence, motion, useReducedMotion } from "motion/react";
import { Alert, AlertDescription, AlertTitle } from "@/components/ui/alert";
import { Badge } from "@/components/ui/badge";
import { Button } from "@/components/ui/button";
import {
  Card,
  CardContent,
  CardDescription,
  CardFooter,
  CardHeader,
  CardTitle,
} from "@/components/ui/card";
import { cn } from "@/lib/utils";
import { formatBalance } from "@/lib/money";
import {
  formatRelativeThai,
  formatRentalDateTime,
  formatRentalDuration,
} from "@/lib/rental-history";
import type { Lease } from "@/lib/machine-contracts";
import { ExtendRentalDialog } from "@/components/my-machines/extend-rental-dialog";
import { CancelRentalButton } from "@/components/my-machines/cancel-rental-button";
import { MoonlightDialog } from "@/components/my-machines/moonlight-dialog";
import { ParsecDialog } from "@/components/my-machines/parsec-dialog";
import { MoonlightMark, ParsecMark } from "@/components/brands/streaming-logos";

type StatusTone = "available" | "busy" | "destructive" | "secondary";

type LeaseStatus = {
  tone: StatusTone;
  badge: string;
  headline: string;
  detail: string;
  headlineClass: string;
};

function leaseStatus(
  lease: Lease,
  now: number,
  parsecReady: boolean,
): LeaseStatus {
  const reachability = lease.agent?.reachability ?? "unknown";
  const address = lease.connection?.address ?? null;
  const stale = lease.connection?.stale === true;
  const lastSeen = formatRelativeThai(lease.agent?.lastSeenAt, now);
  const expiresAt = lease.expiresAtUtc
    ? Date.parse(lease.expiresAtUtc)
    : Number.NaN;
  const expired = Number.isFinite(expiresAt) && expiresAt <= now;
  const channels = parsecReady ? "Moonlight หรือ Parsec" : "Moonlight";

  if (reachability === "unreachable")
    return {
      tone: "destructive",
      badge: "ออฟไลน์",
      headline: "ขาดการติดต่อกับเครื่อง",
      detail: `ติดต่อล่าสุด ${lastSeen}`,
      headlineClass: "text-destructive",
    };
  if (expired)
    return {
      tone: "busy",
      badge: "หมดเวลาแล้ว",
      headline: "หมดเวลาเช่าแล้ว",
      detail: "ระบบกำลังปิดการเช่าและคืนเงินตามเวลาที่เหลือ",
      headlineClass: "text-status-busy",
    };
  if (reachability === "unknown")
    return {
      tone: "secondary",
      badge: "กำลังตรวจสอบ",
      headline: "กำลังตรวจสอบสถานะเครื่อง",
      detail: `ติดต่อล่าสุด ${lastSeen}`,
      headlineClass: "text-muted-foreground",
    };
  if (stale)
    return {
      tone: "busy",
      badge: "ที่อยู่อาจเปลี่ยน",
      headline: "พร้อมใช้งาน",
      detail: "ที่อยู่เชื่อมต่ออาจล้าสมัย กรุณาตรวจสอบกับผู้ดูแล",
      headlineClass: "text-status-busy",
    };
  if (!address)
    return {
      tone: "busy",
      badge: "รอที่อยู่",
      headline: "รอที่อยู่เชื่อมต่อ",
      detail: "ระบบกำลังตรวจสอบ IP สาธารณะสำหรับเชื่อมต่อ",
      headlineClass: "text-status-busy",
    };
  return {
    tone: "available",
    badge: "พร้อมเชื่อมต่อ",
    headline: "กำลังใช้งาน",
    detail: `เชื่อมต่อได้ด้วย ${channels}`,
    headlineClass: "text-status-available",
  };
}

function remainingLabel(expiresAtUtc: string | null, now: number) {
  if (!expiresAtUtc) return "ไม่ทราบเวลา";
  const expiresAt = Date.parse(expiresAtUtc);
  if (!Number.isFinite(expiresAt)) return "ไม่ทราบเวลา";
  const seconds = Math.max(0, Math.floor((expiresAt - now) / 1000));
  if (seconds <= 0) return "หมดเวลาแล้ว";
  if (seconds < 60) return "น้อยกว่า 1 นาที";
  return formatRentalDuration(seconds);
}

function Stat({
  icon,
  label,
  value,
  title,
}: {
  icon: ReactNode;
  label: string;
  value: string;
  title?: string;
}) {
  return (
    <div className="flex min-w-0 flex-col gap-1">
      <dt className="flex items-center gap-1.5 text-xs text-muted-foreground">
        {icon}
        {label}
      </dt>
      <dd className="min-w-0 truncate text-sm font-medium" title={title}>
        {value}
      </dd>
    </div>
  );
}

function ConnectOption({
  title,
  description,
  icon,
  iconClassName,
  badge,
  badgeTone,
  disabled,
  variant,
  onClick,
}: {
  title: string;
  description: string;
  icon: ReactNode;
  iconClassName?: string;
  badge: string;
  badgeTone: "available" | "busy" | "secondary";
  disabled?: boolean;
  /** Brand-colored button style: green Moonlight vs blue→pink Parsec gradient. */
  variant: "success" | "parsec";
  onClick: (event: MouseEvent<HTMLButtonElement>) => void;
}) {
  return (
    <Button
      type="button"
      variant={variant}
      onClick={onClick}
      disabled={disabled}
      className="h-auto w-full items-center justify-start gap-3 rounded-2xl p-3 text-left whitespace-normal"
    >
      <span
        className={cn(
          "grid size-11 shrink-0 place-items-center rounded-full",
          variant === "parsec" ? "bg-white/20 text-white" : "bg-status-available/20 text-status-available",
          iconClassName,
        )}
      >
        {icon}
      </span>
      <span className="flex min-w-0 flex-1 flex-col gap-0.5">
        <span className="flex flex-wrap items-center gap-2 text-sm font-semibold">
          {title}
          <Badge variant={badgeTone}>{badge}</Badge>
        </span>
        <span
          className={cn(
            "text-xs font-normal",
            variant === "parsec" ? "text-white/85" : "text-muted-foreground",
          )}
        >
          {description}
        </span>
      </span>
    </Button>
  );
}

export function ActiveRentalCard({
  lease,
  balanceSatang,
  now,
  extensionNotice,
  onRefresh,
  onExtend,
  onCancelled,
  onMutationStart,
  onMutationEnd,
}: {
  lease: Lease;
  balanceSatang: number;
  now: number;
  extensionNotice: string | null;
  onRefresh: () => void;
  onExtend: (hours: number, idempotencyKey: string) => Promise<void>;
  /** Receives the authoritative post-refund balance returned by the cancel route. */
  onCancelled: (balanceSatang?: number) => void;
  /** Optional hooks so the page can suppress poll-driven refund notices mid-request. */
  onMutationStart?: () => void;
  onMutationEnd?: () => void;
}) {
  const reduceMotion = useReducedMotion();
  const [moonlightOpen, setMoonlightOpen] = useState(false);
  const [parsecOpen, setParsecOpen] = useState(false);
  const [extendOpen, setExtendOpen] = useState(false);
  const moonlightTriggerRef = useRef<HTMLButtonElement | null>(null);
  const parsecTriggerRef = useRef<HTMLButtonElement | null>(null);
  const extendTriggerRef = useRef<HTMLButtonElement | null>(null);

  const name = lease.machineDisplayName || lease.machineId;
  const parsecEnabled = lease.parsec !== null && lease.parsec !== undefined;
  const parsecReady =
    parsecEnabled &&
    lease.parsec?.available === true &&
    Boolean(lease.parsec.publicKey);
  const parsecAttempt = lease.parsec?.attempt ?? null;
  const parsecAttemptRunning =
    parsecAttempt?.state === "queued" ||
    parsecAttempt?.state === "starting" ||
    parsecAttempt?.state === "waiting_email";
  const status = leaseStatus(lease, now, parsecReady);
  const remaining = remainingLabel(lease.expiresAtUtc, now);
  const elapsedSeconds = lease.startsAtUtc
    ? Math.max(0, Math.floor((now - Date.parse(lease.startsAtUtc)) / 1000))
    : null;
  const elapsed =
    elapsedSeconds === null || Number.isNaN(elapsedSeconds)
      ? "—"
      : formatRentalDuration(elapsedSeconds);
  const lastSeenAbsolute = lease.agent?.lastSeenAt
    ? formatRentalDateTime(lease.agent.lastSeenAt)
    : "ไม่มีข้อมูล";
  const lastSeenRelative = formatRelativeThai(lease.agent?.lastSeenAt, now);
  const reachability = lease.agent?.reachability ?? "unknown";
  const moonlightBadge =
    reachability === "reachable"
      ? { label: "พร้อม", tone: "available" as const }
      : reachability === "unreachable"
        ? { label: "รอเครื่องออนไลน์", tone: "busy" as const }
        : { label: "กำลังตรวจสอบ", tone: "secondary" as const };
  const parsecBadge = parsecReady
    ? { label: "พร้อม", tone: "available" as const }
    : { label: "ยังไม่พร้อม", tone: "busy" as const };
  const canExtend =
    lease.state === "active" &&
    lease.pendingRevision === null &&
    lease.payment?.extendable === true;
  const extendDisabledReason =
    !lease.payment
      ? "รายการนี้ไม่มีข้อมูลการชำระเงิน จึงต่อเวลาไม่ได้"
      : !lease.payment.extendable
        ? "รายการเช่านี้ไม่สามารถต่อเวลาได้"
        : null;

  return (
    <motion.section
      initial={reduceMotion ? false : { opacity: 0, y: 12 }}
      animate={{ opacity: 1, y: 0 }}
      transition={
        reduceMotion
          ? { duration: 0 }
          : { duration: 0.24, ease: [0.22, 1, 0.36, 1] }
      }
    >
      <Card
        size="sm"
        className="rounded-3xl border-2 border-primary/25 shadow-none"
      >
        <CardHeader className="bg-gradient-to-b from-primary/[0.07] to-transparent">
          <div className="flex items-center gap-3">
            <span className="grid size-11 shrink-0 place-items-center rounded-2xl bg-primary/10 text-primary">
              <Monitor aria-hidden className="size-5" />
            </span>
            <div className="min-w-0 flex-1">
              <CardTitle>
                <h2 className="truncate text-xl font-bold tracking-tight">
                  {name}
                </h2>
              </CardTitle>
              <CardDescription>
                {lease.startsAtUtc
                  ? `เริ่ม ${formatRentalDateTime(lease.startsAtUtc)}`
                  : "กำลังรอเวลาเริ่มเช่า"}
              </CardDescription>
            </div>
            <Badge variant={status.tone}>
              <span className="size-1.5 rounded-full bg-current" />
              {status.badge}
            </Badge>
          </div>
        </CardHeader>

        <CardContent className="flex flex-col gap-4">
          <div className="grid gap-3 lg:grid-cols-[minmax(0,1.15fr)_minmax(0,1fr)]">
            <div className="flex flex-col justify-between gap-4 rounded-2xl bg-muted/40 p-4">
              <div className="flex flex-col gap-1">
                <p className="text-xs text-muted-foreground">สถานะการใช้งาน</p>
                <AnimatePresence mode="wait" initial={false}>
                  <motion.p
                    key={`${status.headline}:${status.badge}`}
                    initial={reduceMotion ? false : { opacity: 0, y: 6 }}
                    animate={{ opacity: 1, y: 0 }}
                    exit={
                      reduceMotion ? undefined : { opacity: 0, y: -6 }
                    }
                    transition={{ duration: 0.18 }}
                    className={cn(
                      "text-xl font-semibold tracking-tight",
                      status.headlineClass,
                    )}
                  >
                    {status.headline}
                  </motion.p>
                </AnimatePresence>
                <p className="text-sm text-muted-foreground">{status.detail}</p>
              </div>
              <div className="flex flex-wrap items-center gap-x-4 gap-y-1 text-sm">
                <span>
                  เหลืออีก{" "}
                  <span className="font-semibold">{remaining}</span>
                </span>
                <span className="text-muted-foreground">
                  หมดเวลา {formatRentalDateTime(lease.expiresAtUtc)}
                </span>
              </div>
            </div>

            <dl className="grid grid-cols-2 gap-3 rounded-2xl border p-4">
              <Stat
                icon={<Clock aria-hidden className="size-3.5" />}
                label="เวลาที่เช่า"
                value={elapsed}
              />
              <Stat
                icon={<Wallet aria-hidden className="size-3.5" />}
                label="จ่ายแล้ว"
                value={
                  lease.payment
                    ? formatBalance(lease.payment.paidSatang)
                    : "—"
                }
              />
              <Stat
                icon={<Tag aria-hidden className="size-3.5" />}
                label="ราคา/ชม."
                value={
                  lease.payment
                    ? `${formatBalance(lease.payment.hourlyPriceSatang)} / ชม.`
                    : "—"
                }
              />
              <Stat
                icon={<Radio aria-hidden className="size-3.5" />}
                label="ติดต่อล่าสุด"
                value={lastSeenRelative}
                title={lastSeenAbsolute}
              />
            </dl>
          </div>

          {lease.connection?.stale ? (
            <Alert>
              <AlertTriangle aria-hidden />
              <AlertTitle>ที่อยู่เชื่อมต่ออาจล้าสมัย</AlertTitle>
              <AlertDescription>
                ที่อยู่ที่แสดงอาจไม่ใช่ที่อยู่ล่าสุดของเครื่อง —
                ตรวจสอบกับผู้ดูแลก่อนใช้งาน
              </AlertDescription>
            </Alert>
          ) : null}

          {extensionNotice ? (
            <p role="status" className="text-sm text-status-available">
              {extensionNotice}
            </p>
          ) : null}

          {lease.pendingRevision !== null ? (
            <p role="status" className="text-sm text-status-busy">
              กำลังรอเครื่องยืนยันการต่อเวลา
            </p>
          ) : null}

          <section className="flex flex-col gap-2">
            <div className="flex flex-wrap items-baseline justify-between gap-2">
              <h3 className="text-sm font-semibold">เชื่อมต่อเครื่อง</h3>
              <p className="text-xs text-muted-foreground">
                เลือกช่องทางที่ต้องการ
              </p>
            </div>
            <div
              className={cn(
                "grid gap-2",
                parsecEnabled ? "sm:grid-cols-2" : undefined,
              )}
            >
              <ConnectOption
                title="Moonlight"
                description="สตรีมผ่าน Sunshine · PIN 4 หลักใช้ครั้งเดียวต่ออุปกรณ์"
                icon={
                  <MoonlightMark className="size-6" />
                }
                variant="success"
                badge={moonlightBadge.label}
                badgeTone={moonlightBadge.tone}
                onClick={(event) => {
                  moonlightTriggerRef.current = event.currentTarget;
                  setMoonlightOpen(true);
                }}
              />
              {parsecEnabled ? (
                <ConnectOption
                  title="Parsec"
                  description={
                    parsecReady
                      ? "เข้าสู่ระบบด้วยบัญชี Parsec ของคุณเอง"
                      : "เครื่องนี้ยังไม่พร้อมสำหรับ Parsec"
                  }
                  icon={<ParsecMark className="h-6 w-auto" />}
                  variant="parsec"
                  badge={parsecBadge.label}
                  badgeTone={parsecBadge.tone}
                  // Still openable when the Agent hasn't advertised Parsec: the dialog explains
                  // why it is unavailable instead of hiding that reason behind a disabled button.
                  onClick={(event) => {
                    parsecTriggerRef.current = event.currentTarget;
                    setParsecOpen(true);
                  }}
                />
              ) : null}
            </div>
            {parsecEnabled && !parsecReady && parsecAttemptRunning ? (
              <p className="text-xs text-muted-foreground">
                Parsec กำลังอยู่ระหว่างการทำงานของเครื่อง
                กรุณารอสักครู่แล้วรีเฟรช
              </p>
            ) : null}
          </section>

          {extendDisabledReason ? (
            <p className="text-xs text-muted-foreground">
              {extendDisabledReason}
            </p>
          ) : null}
        </CardContent>

        <div className="flex justify-end px-4">
          <CancelRentalButton
            lease={lease}
            onCancelled={onCancelled}
            onMutationStart={onMutationStart}
            onMutationEnd={onMutationEnd}
          />
        </div>

        <CardFooter className="grid grid-cols-2 gap-2 sm:grid-cols-4">
          <Button
            variant="default"
            onClick={(event) => {
              extendTriggerRef.current = event.currentTarget;
              setExtendOpen(true);
            }}
            disabled={!canExtend}
          >
            <Clock data-icon="inline-start" />
            ต่อเวลา
          </Button>
          <Button variant="outline" onClick={onRefresh}>
            <RefreshCw data-icon="inline-start" />
            รีเฟรช
          </Button>
          <Button
            variant="outline"
            nativeButton={false}
            render={<Link href="/history" />}
          >
            <History data-icon="inline-start" />
            ประวัติการเช่า
          </Button>
          <Button
            variant="outline"
            nativeButton={false}
            render={<Link href="/rent" />}
          >
            <Plus data-icon="inline-start" />
            เช่าเครื่องเพิ่ม
          </Button>
        </CardFooter>
      </Card>

      <MoonlightDialog
        lease={lease}
        open={moonlightOpen}
        onClose={() => setMoonlightOpen(false)}
        returnFocusRef={moonlightTriggerRef}
      />
      <ParsecDialog
        lease={lease}
        open={parsecOpen}
        onClose={() => setParsecOpen(false)}
        returnFocusRef={parsecTriggerRef}
      />
      <ExtendRentalDialog
        lease={lease}
        balanceSatang={balanceSatang}
        open={extendOpen}
        onClose={() => setExtendOpen(false)}
        onExtend={onExtend}
        returnFocusRef={extendTriggerRef}
      />
    </motion.section>
  );
}
