"use client";

import { useCallback, useEffect, useState } from "react";
import Link from "next/link";
import { AlertTriangle, History, RefreshCw, Wallet } from "lucide-react";
import { Alert, AlertDescription, AlertTitle } from "@/components/ui/alert";
import { Badge } from "@/components/ui/badge";
import { Button } from "@/components/ui/button";
import {
  Card,
  CardContent,
  CardDescription,
  CardHeader,
  CardTitle,
} from "@/components/ui/card";
import { Spinner } from "@/components/ui/spinner";
import { Footer } from "@/components/ui/layout/Footer";
import { Navbar } from "@/components/ui/layout/Navbar";
import { formatBalance } from "@/lib/money";
import {
  formatRentalDateTime,
  formatRentalDuration,
  rentalStateLabel,
  rentalStateTone,
  type RentalHistoryEntry,
  type RentalHistorySummary,
} from "@/lib/rental-history";

type RentalHistoryResponse = {
  balanceSatang: number;
  controllerAvailable: boolean;
  summary: RentalHistorySummary;
  entries: RentalHistoryEntry[];
};

export function RentalHistory() {
  const [data, setData] = useState<RentalHistoryResponse | null>(null);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState("");

  const load = useCallback(async () => {
    setLoading(true);
    try {
      const response = await fetch("/api/rentals", { cache: "no-store" });
      const payload = (await response.json().catch(() => ({}))) as
        | RentalHistoryResponse
        | { error?: string };
      if (!response.ok) {
        throw new Error("error" in payload && payload.error ? payload.error : "โหลดประวัติการเช่าไม่สำเร็จ");
      }
      setData(payload as RentalHistoryResponse);
      setError("");
    } catch (cause) {
      setError(cause instanceof Error ? cause.message : "โหลดประวัติการเช่าไม่สำเร็จ");
    } finally {
      setLoading(false);
    }
  }, []);

  useEffect(() => {
    const initial = window.setTimeout(() => void load(), 0);
    return () => window.clearTimeout(initial);
  }, [load]);

  const entries = data?.entries ?? [];
  const summary = data?.summary;

  return (
    <div className="flex min-h-screen flex-col">
      <Navbar />
      <main className="mx-auto w-full max-w-5xl flex-1 space-y-6 p-5">
        <header className="flex flex-wrap items-start justify-between gap-4">
          <div>
            <h1 className="flex items-center gap-2 text-3xl font-bold">
              <History aria-hidden className="size-7 text-primary" />
              ประวัติการเช่า
            </h1>
            <p className="text-sm text-muted-foreground">
              รายการเช่า ยอดชำระ และเงินคืนของคุณทั้งหมด
            </p>
          </div>
          <div className="flex items-center gap-2">
            <div className="flex items-center gap-2 rounded-xl border px-3 py-2 text-sm">
              <Wallet aria-hidden className="size-4 text-muted-foreground" />
              <span className="text-muted-foreground">ยอดเงินคงเหลือ</span>
              <span className="font-medium tabular-nums">
                {formatBalance(data?.balanceSatang ?? 0)}
              </span>
            </div>
            <Button variant="outline" size="sm" onClick={() => void load()} disabled={loading}>
              {loading ? <Spinner data-icon="inline-start" /> : <RefreshCw data-icon="inline-start" />}
              รีเฟรช
            </Button>
          </div>
        </header>

        {summary ? <SummaryCards summary={summary} /> : null}

        {data && !data.controllerAvailable ? (
          <Alert>
            <AlertTriangle aria-hidden />
            <AlertTitle>แสดงข้อมูลการชำระเงินเท่านั้น</AlertTitle>
            <AlertDescription>
              ติดต่อระบบเครื่องไม่ได้ในขณะนี้ สถานะและเวลาของรายการอาจไม่เป็นปัจจุบัน
            </AlertDescription>
          </Alert>
        ) : null}

        {error ? (
          <Alert variant="destructive">
            <AlertTriangle aria-hidden />
            <AlertTitle>โหลดประวัติการเช่าไม่สำเร็จ</AlertTitle>
            <AlertDescription>{error}</AlertDescription>
          </Alert>
        ) : null}

        <p role="status" className="sr-only">
          {loading ? "กำลังโหลดประวัติการเช่า" : `พบ ${entries.length} รายการ`}
        </p>

        {loading && !data ? (
          <p className="flex items-center gap-2 text-sm text-muted-foreground">
            <Spinner data-icon="inline-start" />
            กำลังโหลดประวัติการเช่า…
          </p>
        ) : entries.length === 0 ? (
          <div className="flex flex-col items-center gap-3 rounded-2xl border border-dashed p-10 text-center">
            <History aria-hidden className="size-8 text-muted-foreground" />
            <p className="text-lg font-medium">ยังไม่มีประวัติการเช่า</p>
            <p className="text-sm text-muted-foreground">
              เลือกเครื่องที่เปิดให้เช่าแล้วเริ่มใช้งานได้เลย
            </p>
            <Button nativeButton={false} render={<Link href="/rent" />}>ดูเครื่องที่เปิดให้เช่า</Button>
          </div>
        ) : (
          <div className="flex flex-col gap-3">
            {entries.map((entry) => (
              <RentalRow key={entry.rentalId} entry={entry} />
            ))}
          </div>
        )}
      </main>
      <Footer />
    </div>
  );
}

function SummaryCards({ summary }: { summary: RentalHistorySummary }) {
  const items = [
    { label: "รายการทั้งหมด", value: summary.totalRentals.toLocaleString("th-TH") },
    { label: "กำลังเช่า", value: summary.activeRentals.toLocaleString("th-TH") },
    { label: "ชำระรวม", value: formatBalance(summary.paidSatang) },
    { label: "คืนเงินรวม", value: formatBalance(summary.refundedSatang) },
    { label: "สุทธิ", value: formatBalance(summary.netSatang) },
  ];
  return (
    <dl className="grid grid-cols-2 gap-3 sm:grid-cols-3 lg:grid-cols-5">
      {items.map((item) => (
        <div key={item.label} className="rounded-2xl border bg-card p-4">
          <dt className="text-xs text-muted-foreground">{item.label}</dt>
          <dd className="mt-1 text-lg font-semibold tabular-nums">{item.value}</dd>
        </div>
      ))}
    </dl>
  );
}

function RentalRow({ entry }: { entry: RentalHistoryEntry }) {
  const active =
    entry.state === "active" || entry.state === "preparing" || entry.state === "ending";
  const endLabel = entry.endedAtUtc
    ? formatRentalDateTime(entry.endedAtUtc)
    : entry.expiresAtUtc
      ? formatRentalDateTime(entry.expiresAtUtc)
      : "—";
  return (
    <Card size="sm" className="rounded-2xl border shadow-none ring-0">
      <CardHeader>
        <div className="flex flex-wrap items-center justify-between gap-2">
          <CardTitle className="truncate">{entry.machineName}</CardTitle>
          <Badge variant={rentalStateTone(entry.state)}>
            {rentalStateLabel(entry.state)}
          </Badge>
        </div>
        <CardDescription>
          {entry.startsAtUtc
            ? `${formatRentalDateTime(entry.startsAtUtc)} – ${endLabel}`
            : entry.state === "pending"
              ? "กำลังยืนยันการเช่ากับเครื่อง"
              : "ยังไม่เริ่มนับเวลา"}
        </CardDescription>
      </CardHeader>
      <CardContent className="flex flex-col gap-3">
        <dl className="grid grid-cols-2 gap-3 text-sm sm:grid-cols-4">
          <div>
            <dt className="text-muted-foreground">ระยะเวลาที่ชำระ</dt>
            <dd className="font-medium">
              {entry.paidSatang === null
                ? "—"
                : formatRentalDuration(entry.bookedSeconds + entry.extensionSeconds)}
            </dd>
          </div>
          <div>
            <dt className="text-muted-foreground">ราคา/ชม.</dt>
            <dd className="font-medium tabular-nums">
              {entry.hourlyPriceSatang === null ? "—" : formatBalance(entry.hourlyPriceSatang)}
            </dd>
          </div>
          <div>
            <dt className="text-muted-foreground">ชำระ</dt>
            <dd className="font-medium tabular-nums">
              {entry.paidSatang === null ? "—" : formatBalance(entry.paidSatang)}
            </dd>
          </div>
          <div>
            <dt className="text-muted-foreground">คืนเงิน</dt>
            <dd className="font-medium tabular-nums">
              {formatBalance(entry.refundedSatang)}
            </dd>
          </div>
        </dl>
        <div className="flex flex-wrap items-center justify-between gap-2">
          <p className="text-sm">
            สุทธิ{" "}
            <span className="font-medium tabular-nums">
              {entry.netSatang === null ? "—" : formatBalance(entry.netSatang)}
            </span>
          </p>
          {active ? (
            <Button variant="outline" size="sm" nativeButton={false} render={<Link href="/my-machines" />}>
              ดูเครื่องที่กำลังเช่า
            </Button>
          ) : null}
        </div>
      </CardContent>
    </Card>
  );
}
