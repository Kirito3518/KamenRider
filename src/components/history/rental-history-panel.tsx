"use client";

import { useCallback, useEffect, useState, type FormEvent } from "react";
import Link from "next/link";
import {
  ChevronLeft,
  ChevronRight,
  CircleAlert,
  Eye,
  MonitorPlay,
  RefreshCw,
  Search,
} from "lucide-react";
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
import { Skeleton } from "@/components/ui/skeleton";
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
  rentalDisplayState,
  rentalDisplayStateLabel,
  rentalDisplayStateTone,
  type RentalDisplayState,
  type RentalHistoryEntry,
  type RentalHistoryResponse,
  type RentalHistorySort,
} from "@/lib/rental-history";
import {
  HistoryRangePicker,
  type HistoryRangePeriod,
} from "@/components/history/history-range-picker";
import { RentalDetailDialog } from "@/components/history/rental-detail-dialog";
import {
  clientErrorMessage,
  topupFetch,
} from "@/components/topup/client";

const stateOptions: { value: RentalDisplayState | "all"; label: string }[] = [
  { value: "all", label: "ทุกสถานะ" },
  { value: "active", label: "กำลังเช่า" },
  { value: "completed", label: "เสร็จสิ้น" },
  { value: "expired", label: "หมดอายุ" },
  { value: "cancelled", label: "ยกเลิก/ไม่สำเร็จ" },
  { value: "pending", label: "รอยืนยัน" },
  { value: "unknown", label: "ไม่ทราบสถานะ" },
];

export function RentalHistoryPanel({
  onBalance,
}: {
  onBalance?: (balanceSatang: number) => void;
}) {
  const [data, setData] = useState<RentalHistoryResponse | null>(null);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState("");
  const [queryInput, setQueryInput] = useState("");
  const [filters, setFilters] = useState({
    q: "",
    state: "all" as RentalDisplayState | "all",
    machineId: "all",
    period: "all" as HistoryRangePeriod,
    from: "",
    to: "",
    sort: "newest" as RentalHistorySort,
    page: 1,
  });
  const [selected, setSelected] = useState<RentalHistoryEntry | null>(null);
  const [detailOpen, setDetailOpen] = useState(false);

  const load = useCallback(async (signal?: AbortSignal) => {
    setLoading(true);
    try {
      const params = new URLSearchParams();
      if (filters.q) params.set("q", filters.q);
      if (filters.state !== "all") params.set("state", filters.state);
      if (filters.machineId !== "all") params.set("machineId", filters.machineId);
      params.set("period", filters.period);
      if (filters.period === "custom") {
        if (filters.from) params.set("from", filters.from);
        if (filters.to) params.set("to", filters.to);
      }
      params.set("sort", filters.sort);
      params.set("page", String(filters.page));
      const response = await topupFetch<RentalHistoryResponse>(
        `/api/rentals?${params.toString()}`,
        { signal },
      );
      if (signal?.aborted) return;
      setData(response);
      setError("");
      onBalance?.(response.balanceSatang);
    } catch (cause) {
      if (!signal?.aborted) setError(clientErrorMessage(cause));
    } finally {
      if (!signal?.aborted) setLoading(false);
    }
  }, [filters, onBalance]);

  useEffect(() => {
    const controller = new AbortController();
    const timer = window.setTimeout(() => void load(controller.signal), 0);
    return () => {
      window.clearTimeout(timer);
      controller.abort();
    };
  }, [load]);

  function update(partial: Partial<typeof filters>, resetPage = true) {
    setFilters((current) => ({
      ...current,
      ...partial,
      ...(resetPage ? { page: 1 } : {}),
    }));
  }

  function submitSearch(event: FormEvent<HTMLFormElement>) {
    event.preventDefault();
    update({ q: queryInput.trim() });
  }

  const entries = data?.entries ?? [];
  const summary = data?.summary;
  const machines = data?.machines ?? [];
  const hasFilters =
    Boolean(filters.q) ||
    filters.state !== "all" ||
    filters.machineId !== "all" ||
    filters.period !== "all";

  return (
    <div className="flex flex-col gap-4">
      {summary ? (
        <dl className="grid grid-cols-2 gap-3 sm:grid-cols-3 lg:grid-cols-5">
          {[
            { label: "รายการทั้งหมด", value: summary.totalRentals.toLocaleString("th-TH") },
            { label: "กำลังเช่า", value: summary.activeRentals.toLocaleString("th-TH") },
            { label: "ชำระรวม", value: formatBalance(summary.paidSatang) },
            { label: "คืนเงินรวม", value: formatBalance(summary.refundedSatang) },
            { label: "สุทธิ", value: formatBalance(summary.netSatang) },
          ].map((item) => (
            <div key={item.label} className="rounded-2xl border bg-card p-3">
              <dt className="text-xs text-muted-foreground">{item.label}</dt>
              <dd className="mt-1 text-lg font-semibold tabular-nums">{item.value}</dd>
            </div>
          ))}
        </dl>
      ) : null}

      {data && !data.controllerAvailable ? (
        <Alert>
          <CircleAlert aria-hidden />
          <AlertTitle>ติดต่อระบบเครื่องไม่ได้</AlertTitle>
          <AlertDescription>
            กำลังแสดงเฉพาะข้อมูลการชำระเงิน สถานะและเวลาจาก Controller อาจไม่เป็นปัจจุบัน
          </AlertDescription>
        </Alert>
      ) : null}

      <div className="flex flex-col gap-3 rounded-2xl border bg-card p-4">
        <div className="flex flex-wrap items-center gap-2">
          <form onSubmit={submitSearch} role="search" className="flex w-full max-w-sm items-center gap-2">
            <Input
              type="search"
              value={queryInput}
              onChange={(event) => setQueryInput(event.currentTarget.value)}
              placeholder="ค้นหาเครื่องหรือรหัสรายการ"
              aria-label="ค้นหาประวัติการเช่า"
              maxLength={100}
            />
            <Button type="submit" variant="outline" size="icon-sm" aria-label="ค้นหา">
              <Search />
            </Button>
          </form>
          <Select
            items={stateOptions}
            value={filters.state}
            onValueChange={(value) =>
              update({ state: (value as RentalDisplayState | "all") ?? "all" })
            }
          >
            <SelectTrigger aria-label="กรองสถานะการเช่า" className="w-full sm:w-44">
              <SelectValue />
            </SelectTrigger>
            <SelectContent>
              <SelectGroup>
                {stateOptions.map((option) => (
                  <SelectItem key={option.value} value={option.value}>
                    {option.label}
                  </SelectItem>
                ))}
              </SelectGroup>
            </SelectContent>
          </Select>
          <Select
            items={[
              { value: "all", label: "ทุกเครื่อง" },
              ...machines.map((machine) => ({
                value: machine.machineId,
                label: machine.displayName,
              })),
            ]}
            value={filters.machineId}
            onValueChange={(value) => update({ machineId: value ?? "all" })}
          >
            <SelectTrigger aria-label="กรองเครื่องที่เช่า" className="w-full sm:w-44">
              <SelectValue />
            </SelectTrigger>
            <SelectContent>
              <SelectGroup>
                <SelectItem value="all">ทุกเครื่อง</SelectItem>
                {machines.map((machine) => (
                  <SelectItem key={machine.machineId} value={machine.machineId}>
                    {machine.displayName}
                  </SelectItem>
                ))}
              </SelectGroup>
            </SelectContent>
          </Select>
          <HistoryRangePicker
            period={filters.period}
            from={filters.from}
            to={filters.to}
            allowAll
            disabled={loading}
            onChange={(value) => update(value)}
          />
          <Button variant="outline" size="sm" onClick={() => void load()} disabled={loading}>
            {loading ? <Spinner data-icon="inline-start" /> : <RefreshCw data-icon="inline-start" />}
            รีเฟรช
          </Button>
          {hasFilters ? (
            <Button
              variant="ghost"
              size="sm"
              onClick={() => {
                setQueryInput("");
                setFilters((current) => ({
                  ...current,
                  q: "",
                  state: "all",
                  machineId: "all",
                  period: "all",
                  from: "",
                  to: "",
                  page: 1,
                }));
              }}
            >
              ล้างตัวกรอง
            </Button>
          ) : null}
        </div>
        {data?.rangeError ? (
          <Alert variant="destructive">
            <CircleAlert aria-hidden />
            <AlertDescription>{data.rangeError}</AlertDescription>
          </Alert>
        ) : null}
      </div>

      {loading && !data ? (
        <div className="flex flex-col gap-2">
          {Array.from({ length: 5 }).map((_, index) => (
            <Skeleton key={index} className="h-12 w-full" />
          ))}
        </div>
      ) : error ? (
        <Alert variant="destructive">
          <CircleAlert aria-hidden />
          <AlertTitle>โหลดประวัติการเช่าไม่สำเร็จ</AlertTitle>
          <AlertDescription>
            <p>{error}</p>
            <Button variant="outline" size="sm" className="mt-2" onClick={() => void load()}>
              ลองใหม่
            </Button>
          </AlertDescription>
        </Alert>
      ) : entries.length === 0 ? (
        <Empty className="py-10">
          <EmptyHeader>
            <EmptyMedia variant="icon">
              <MonitorPlay aria-hidden />
            </EmptyMedia>
            <EmptyTitle>
              {hasFilters ? "ไม่พบรายการที่ตรงกับตัวกรอง" : "ยังไม่มีประวัติการเช่า"}
            </EmptyTitle>
            <EmptyDescription>
              {hasFilters
                ? "ลองเปลี่ยนคำค้นหาหรือช่วงเวลา"
                : "เลือกเครื่องที่เปิดให้เช่าแล้วเริ่มใช้งานได้เลย"}
            </EmptyDescription>
          </EmptyHeader>
          {!hasFilters ? (
            <Button nativeButton={false} render={<Link href="/rent" />}>
              <MonitorPlay data-icon="inline-start" />
              ดูเครื่องที่เปิดให้เช่า
            </Button>
          ) : null}
        </Empty>
      ) : (
        <div className="rounded-2xl border bg-card">
          <div className="overflow-x-auto">
            <Table>
              <caption className="sr-only">ประวัติการเช่าของคุณ</caption>
              <TableHeader>
                <TableRow>
                  <TableHead>เครื่อง</TableHead>
                  <TableHead>ช่วงเวลา</TableHead>
                  <TableHead>ระยะเวลา</TableHead>
                  <TableHead className="text-right">ชำระ</TableHead>
                  <TableHead className="text-right">คืนเงิน</TableHead>
                  <TableHead className="text-right">สุทธิ</TableHead>
                  <TableHead>สถานะ</TableHead>
                  <TableHead className="text-right">รายละเอียด</TableHead>
                </TableRow>
              </TableHeader>
              <TableBody>
                {entries.map((entry) => {
                  const state = rentalDisplayState(entry);
                  return (
                    <TableRow key={entry.rentalId}>
                      <TableCell className="min-w-32">
                        <span className="block max-w-40 truncate font-medium">
                          {entry.machineName}
                        </span>
                        <span className="block font-mono text-xs text-muted-foreground">
                          {entry.rentalId}
                        </span>
                      </TableCell>
                      <TableCell className="whitespace-nowrap text-sm">
                        {entry.startsAtUtc
                          ? `${formatRentalDateTime(entry.startsAtUtc)} – ${
                              entry.endedAtUtc
                                ? formatRentalDateTime(entry.endedAtUtc)
                                : entry.expiresAtUtc
                                  ? formatRentalDateTime(entry.expiresAtUtc)
                                  : "—"
                            }`
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
                      <TableCell>
                        <Badge variant={rentalDisplayStateTone(state)}>
                          {rentalDisplayStateLabel(state)}
                        </Badge>
                      </TableCell>
                      <TableCell className="text-right">
                        <Button
                          variant="outline"
                          size="icon-sm"
                          aria-label={`ดูรายละเอียด ${entry.machineName}`}
                          title="ดูรายละเอียด"
                          onClick={() => {
                            setSelected(entry);
                            setDetailOpen(true);
                          }}
                        >
                          <Eye />
                        </Button>
                      </TableCell>
                    </TableRow>
                  );
                })}
              </TableBody>
            </Table>
          </div>
          <div className="flex flex-wrap items-center justify-between gap-3 border-t p-3 text-xs text-muted-foreground">
            <p>
              พบ {data?.pagination.total.toLocaleString("th-TH") ?? 0} รายการ · หน้า{" "}
              {data?.pagination.page ?? 1}/{data?.pagination.pageCount ?? 1}
            </p>
            <div className="flex items-center gap-2">
              <Button
                variant="outline"
                size="sm"
                onClick={() =>
                  setFilters((current) => ({ ...current, page: Math.max(1, current.page - 1) }))
                }
                disabled={loading || (data?.pagination.page ?? 1) <= 1}
              >
                <ChevronLeft data-icon="inline-start" />
                ก่อนหน้า
              </Button>
              <Button
                variant="outline"
                size="sm"
                onClick={() =>
                  setFilters((current) => ({ ...current, page: current.page + 1 }))
                }
                disabled={
                  loading || (data ? data.pagination.page >= data.pagination.pageCount : true)
                }
              >
                ถัดไป
                <ChevronRight data-icon="inline-end" />
              </Button>
            </div>
          </div>
        </div>
      )}

      <RentalDetailDialog
        entry={selected}
        open={detailOpen}
        onOpenChange={(open) => {
          setDetailOpen(open);
          if (!open) setSelected(null);
        }}
      />
    </div>
  );
}
