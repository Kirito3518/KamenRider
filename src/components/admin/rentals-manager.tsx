"use client";

import { useCallback, useEffect, useState } from "react";
import { AlertTriangle, History, RefreshCw, Wallet } from "lucide-react";
import {
  AdminCard,
  AdminPage,
  AdminPageHeader,
  AdminStatCard,
  AdminStatGrid,
} from "@/components/admin/admin-page";
import { Alert, AlertDescription, AlertTitle } from "@/components/ui/alert";
import { Badge } from "@/components/ui/badge";
import { Button } from "@/components/ui/button";
import {
  Empty,
  EmptyDescription,
  EmptyHeader,
  EmptyMedia,
  EmptyTitle,
} from "@/components/ui/empty";
import { Input } from "@/components/ui/input";
import {
  Select,
  SelectContent,
  SelectGroup,
  SelectItem,
  SelectTrigger,
  SelectValue,
} from "@/components/ui/select";
import { Spinner } from "@/components/ui/spinner";
import {
  Table,
  TableBody,
  TableCell,
  TableHead,
  TableHeader,
  TableRow,
} from "@/components/ui/table";
import { formatBalance } from "@/lib/money";
import {
  formatRentalDateTime,
  formatRentalDuration,
  rentalStateLabel,
  rentalStateTone,
  type AdminRentalHistoryEntry,
  type RentalHistorySummary,
} from "@/lib/rental-history";

type AdminRentalsResponse = {
  controllerAvailable: boolean;
  summary: RentalHistorySummary;
  entries: AdminRentalHistoryEntry[];
};

const stateFilters = [
  { value: "all", label: "ทุกสถานะ" },
  { value: "preparing", label: "กำลังเตรียมเครื่อง" },
  { value: "active", label: "กำลังเช่า" },
  { value: "ending", label: "กำลังสิ้นสุด" },
  { value: "completed", label: "สิ้นสุดแล้ว" },
  { value: "failed", label: "ไม่สำเร็จ" },
  { value: "unknown", label: "ไม่ทราบสถานะ" },
];

export function RentalsManager() {
  const [data, setData] = useState<AdminRentalsResponse | null>(null);
  const [loading, setLoading] = useState(true);
  const [loadError, setLoadError] = useState("");
  const [query, setQuery] = useState("");
  const [stateFilter, setStateFilter] = useState("all");

  const load = useCallback(async () => {
    setLoading(true);
    try {
      const response = await fetch("/api/admin/rentals", { cache: "no-store" });
      const payload = (await response.json().catch(() => ({}))) as
        | AdminRentalsResponse
        | { error?: string };
      if (!response.ok) {
        throw new Error("error" in payload && payload.error ? payload.error : undefined);
      }
      setData(payload as AdminRentalsResponse);
      setLoadError("");
    } catch (cause) {
      setLoadError(cause instanceof Error && cause.message ? cause.message : "โหลดประวัติการเช่าไม่สำเร็จ");
    } finally {
      setLoading(false);
    }
  }, []);

  useEffect(() => {
    const initial = window.setTimeout(() => void load(), 0);
    return () => window.clearTimeout(initial);
  }, [load]);

  const entries = data?.entries ?? [];
  const needle = query.trim().toLowerCase();
  const filtered = entries.filter((entry) => {
    if (stateFilter !== "all" && entry.state !== stateFilter) return false;
    if (!needle) return true;
    return [entry.machineName, entry.userName, entry.userEmail, entry.rentalId]
      .join(" ")
      .toLowerCase()
      .includes(needle);
  });

  return (
    <AdminPage>
      <AdminPageHeader
        icon={History}
        title="ประวัติการเช่า"
        description="รายการเช่าล่าสุดพร้อมยอดชำระและเงินคืน · ระบบคืนเงินตามเวลาที่ไม่ได้ใช้ให้อัตโนมัติ"
        actions={
          <Button type="button" variant="outline" size="sm" disabled={loading} onClick={() => void load()}>
            {loading ? <Spinner data-icon="inline-start" /> : <RefreshCw data-icon="inline-start" />}
            รีเฟรช
          </Button>
        }
      />

      {loadError ? (
        <Alert variant="destructive">
          <AlertTriangle aria-hidden />
          <AlertTitle>โหลดประวัติการเช่าไม่สำเร็จ</AlertTitle>
          <AlertDescription>
            <p>{loadError}</p>
            <Button
              type="button"
              variant="outline"
              size="sm"
              className="mt-2"
              disabled={loading}
              onClick={() => void load()}
            >
              ลองใหม่
            </Button>
          </AlertDescription>
        </Alert>
      ) : null}

      {data && !data.controllerAvailable ? (
        <Alert>
          <AlertTriangle aria-hidden />
          <AlertTitle>ติดต่อระบบเครื่องไม่ได้</AlertTitle>
          <AlertDescription>
            กำลังแสดงเฉพาะข้อมูลการชำระเงิน สถานะและเวลาจาก Controller อาจไม่เป็นปัจจุบัน
          </AlertDescription>
        </Alert>
      ) : null}

      <AdminStatGrid>
        <AdminStatCard
          label="กำลังเช่า"
          value={data ? data.summary.activeRentals.toLocaleString("th-TH") : "—"}
          hint="รายการที่เตรียม/กำลังใช้งาน/กำลังสิ้นสุด"
          icon={History}
          tone="busy"
        />
        <AdminStatCard
          label="รายการล่าสุด"
          value={data ? data.summary.totalRentals.toLocaleString("th-TH") : "—"}
          hint="แสดงรายการล่าสุดสูงสุด 500 รายการ"
          icon={History}
          tone="primary"
        />
        <AdminStatCard
          label="รายได้สุทธิ"
          value={data ? formatBalance(data.summary.netSatang) : "—"}
          hint="ยอดชำระหลังหักเงินคืน (ทั้งหมด)"
          icon={Wallet}
          tone="available"
        />
        <AdminStatCard
          label="คืนเงินแล้ว"
          value={data ? formatBalance(data.summary.refundedSatang) : "—"}
          hint="คืนตามเวลาที่ไม่ได้ใช้ (ทั้งหมด)"
          icon={Wallet}
          tone="maintenance"
        />
      </AdminStatGrid>

      <AdminCard>
        <div className="flex flex-col gap-3 border-b p-4 sm:flex-row sm:items-center sm:justify-between">
          <div className="flex flex-1 flex-col gap-2 sm:flex-row sm:items-center">
            <Input
              type="search"
              value={query}
              onChange={(event) => setQuery(event.currentTarget.value)}
              placeholder="ค้นหาชื่อผู้เช่า อีเมล เครื่อง หรือรหัสรายการ"
              aria-label="ค้นหาประวัติการเช่า"
              className="sm:max-w-sm"
            />
            <Select
              items={stateFilters}
              value={stateFilter}
              onValueChange={(value) => setStateFilter(value ?? "all")}
            >
              <SelectTrigger aria-label="กรองสถานะการเช่า" className="w-full sm:w-48">
                <SelectValue />
              </SelectTrigger>
              <SelectContent>
                <SelectGroup>
                  {stateFilters.map((option) => (
                    <SelectItem key={option.value} value={option.value}>
                      {option.label}
                    </SelectItem>
                  ))}
                </SelectGroup>
              </SelectContent>
            </Select>
          </div>
          <p className="text-xs text-muted-foreground" role="status" aria-live="polite">
            {loading && !data ? "กำลังโหลด…" : `แสดง ${filtered.length} จาก ${entries.length} รายการ`}
          </p>
        </div>

        {loading && !data ? (
          <p className="flex items-center gap-2 p-6 text-sm text-muted-foreground">
            <Spinner data-icon="inline-start" />
            กำลังโหลดประวัติการเช่า…
          </p>
        ) : entries.length === 0 ? (
          <Empty className="border-0 py-10">
            <EmptyHeader>
              <EmptyMedia variant="icon">
                <History aria-hidden />
              </EmptyMedia>
              <EmptyTitle>ยังไม่มีประวัติการเช่า</EmptyTitle>
              <EmptyDescription>
                เมื่อมีผู้เช่าเครื่อง รายการและยอดชำระจะแสดงที่นี่
              </EmptyDescription>
            </EmptyHeader>
          </Empty>
        ) : filtered.length === 0 ? (
          <p className="p-6 text-sm text-muted-foreground">
            ไม่พบรายการที่ตรงกับตัวกรอง ลองล้างคำค้นหาหรือเลือกสถานะอื่น
          </p>
        ) : (
          <div className="overflow-x-auto">
            <Table>
              <caption className="sr-only">ประวัติการเช่าเครื่อง</caption>
              <TableHeader>
                <TableRow>
                  <TableHead>ผู้เช่า</TableHead>
                  <TableHead>เครื่อง</TableHead>
                  <TableHead>สถานะ</TableHead>
                  <TableHead>เริ่ม</TableHead>
                  <TableHead>สิ้นสุด</TableHead>
                  <TableHead>ระยะเวลา</TableHead>
                  <TableHead className="text-right">ชำระ</TableHead>
                  <TableHead className="text-right">คืนเงิน</TableHead>
                  <TableHead className="text-right">สุทธิ</TableHead>
                </TableRow>
              </TableHeader>
              <TableBody>
                {filtered.map((entry) => (
                  <TableRow key={entry.rentalId}>
                    <TableCell className="min-w-40">
                      <div className="flex flex-col gap-0.5">
                        <span className="font-medium">{entry.userName || entry.userId}</span>
                        <span className="truncate text-xs text-muted-foreground">
                          {entry.userEmail || entry.userId}
                        </span>
                      </div>
                    </TableCell>
                    <TableCell className="min-w-32">
                      <div className="flex flex-col gap-0.5">
                        <span>{entry.machineName}</span>
                        <span className="font-mono text-xs text-muted-foreground">{entry.machineId}</span>
                      </div>
                    </TableCell>
                    <TableCell>
                      <Badge variant={rentalStateTone(entry.state)}>
                        {rentalStateLabel(entry.state)}
                      </Badge>
                    </TableCell>
                    <TableCell className="whitespace-nowrap text-sm">
                      {entry.startsAtUtc ? formatRentalDateTime(entry.startsAtUtc) : "—"}
                    </TableCell>
                    <TableCell className="whitespace-nowrap text-sm">
                      {entry.endedAtUtc
                        ? formatRentalDateTime(entry.endedAtUtc)
                        : entry.expiresAtUtc
                          ? `${formatRentalDateTime(entry.expiresAtUtc)} (กำหนด)`
                          : "—"}
                    </TableCell>
                    <TableCell className="whitespace-nowrap text-sm">
                      {entry.paidSatang === null
                        ? "—"
                        : formatRentalDuration(entry.bookedSeconds + entry.extensionSeconds)}
                    </TableCell>
                    <TableCell className="text-right tabular-nums">
                      {entry.paidSatang === null ? "—" : formatBalance(entry.paidSatang)}
                    </TableCell>
                    <TableCell className="text-right tabular-nums">
                      {formatBalance(entry.refundedSatang)}
                    </TableCell>
                    <TableCell className="text-right font-medium tabular-nums">
                      {entry.netSatang === null ? "—" : formatBalance(entry.netSatang)}
                    </TableCell>
                  </TableRow>
                ))}
              </TableBody>
            </Table>
          </div>
        )}
      </AdminCard>

      <p role="status" aria-live="polite" className="sr-only">
        {loadError ? loadError : undefined}
      </p>
    </AdminPage>
  );
}
