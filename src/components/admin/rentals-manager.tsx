"use client";

import { useCallback, useEffect, useState, type FormEvent } from "react";
import {
  ArrowDownUp,
  ChevronLeft,
  ChevronRight,
  CircleAlert,
  Clock3,
  Eye,
  History,
  MonitorPlay,
  RefreshCw,
  Search,
  Wallet,
} from "lucide-react";
import { motion, useReducedMotion } from "motion/react";
import {
  AdminCard,
  AdminPage,
  AdminPageHeader,
  AdminStatCard,
  AdminStatGrid,
} from "@/components/admin/admin-page";
import { RentalCharts } from "@/components/admin/rental-charts";
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
  InlineSkeleton,
  LoadingRegion,
  SkeletonBlock,
  TableSkeleton,
} from "@/components/loading";
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
  type AdminRentalHistoryEntry,
  type AdminRentalHistoryResponse,
  type AdminRentalStats,
  type RentalDisplayState,
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

const EMPTY_STATS: AdminRentalStats = {
  totalRentals: 0,
  activeRentals: 0,
  completedRentals: 0,
  expiredRentals: 0,
  revenueSatang: 0,
  rentalCount: 0,
};

const stateOptions: { value: RentalDisplayState | "all"; label: string }[] = [
  { value: "all", label: "ทุกสถานะ" },
  { value: "active", label: "กำลังเช่า" },
  { value: "completed", label: "เสร็จสิ้น" },
  { value: "expired", label: "หมดอายุ" },
  { value: "cancelled", label: "ยกเลิก/ไม่สำเร็จ" },
  { value: "pending", label: "รอยืนยัน" },
  { value: "unknown", label: "ไม่ทราบสถานะ" },
];

function percentageHint(current: number, previous: number) {
  if (previous <= 0) return "ยังไม่มีข้อมูลเปรียบเทียบ";
  const change = ((current - previous) / previous) * 100;
  return `${change >= 0 ? "+" : ""}${change.toFixed(1)}% เทียบช่วงก่อนหน้า`;
}


export function RentalsManager() {
  const reduceMotion = useReducedMotion();
  const [data, setData] = useState<AdminRentalHistoryResponse | null>(null);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState("");
  const [queryInput, setQueryInput] = useState("");
  const [filters, setFilters] = useState({
    q: "",
    state: "all" as RentalDisplayState | "all",
    machineId: "all",
    period: "30d" as HistoryRangePeriod,
    from: "",
    to: "",
    sort: "newest" as RentalHistorySort,
    page: 1,
  });
  const [selected, setSelected] = useState<AdminRentalHistoryEntry | null>(null);
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
      const response = await topupFetch<AdminRentalHistoryResponse>(
        `/api/admin/rentals?${params.toString()}`,
        { signal },
      );
      if (signal?.aborted) return;
      setData(response);
      setError("");
    } catch (cause) {
      if (!signal?.aborted) setError(clientErrorMessage(cause));
    } finally {
      if (!signal?.aborted) setLoading(false);
    }
  }, [filters]);

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

  const stats = data?.stats ?? EMPTY_STATS;
  const previous = data?.previous ?? EMPTY_STATS;
  const entries = data?.entries ?? [];
  const machines = data?.machines ?? [];
  const hasFilters =
    Boolean(filters.q) ||
    filters.state !== "all" ||
    filters.machineId !== "all";

  const cards = [
    {
      key: "total",
      label: "การเช่าทั้งหมด",
      value: stats.totalRentals.toLocaleString("th-TH"),
      hint: percentageHint(stats.totalRentals, previous.totalRentals),
      icon: History,
      tone: "muted" as const,
    },
    {
      key: "active",
      label: "กำลังเช่า",
      value: stats.activeRentals.toLocaleString("th-TH"),
      hint: "รายการที่กำลังดำเนินการอยู่ในปัจจุบัน",
      icon: Clock3,
      tone: "busy" as const,
    },
    {
      key: "completed",
      label: "เสร็จสิ้น",
      value: stats.completedRentals.toLocaleString("th-TH"),
      hint: percentageHint(stats.completedRentals, previous.completedRentals),
      icon: MonitorPlay,
      tone: "available" as const,
    },
    {
      key: "expired",
      label: "หมดอายุ",
      value: stats.expiredRentals.toLocaleString("th-TH"),
      hint: percentageHint(stats.expiredRentals, previous.expiredRentals),
      icon: Clock3,
      tone: "maintenance" as const,
    },
    {
      key: "revenue",
      label: "รายได้สุทธิในช่วง",
      value: formatBalance(stats.revenueSatang),
      hint: percentageHint(stats.revenueSatang, previous.revenueSatang),
      icon: Wallet,
      tone: "primary" as const,
    },
  ];

  return (
    <AdminPage>
      <AdminPageHeader
        icon={History}
        title="ประวัติการเช่า"
        description="ภาพรวมรายได้ จำนวนการเช่า และรายละเอียดย้อนหลัง · ระบบคืนเงินตามเวลาที่ไม่ได้ใช้ให้อัตโนมัติ"
        actions={
          <Button type="button" variant="outline" size="sm" disabled={loading} onClick={() => void load()}>
            {loading ? <Spinner data-icon="inline-start" /> : <RefreshCw data-icon="inline-start" />}
            รีเฟรช
          </Button>
        }
      />

      {error ? (
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
              placeholder="ค้นหาชื่อผู้เช่า อีเมล รหัส หรือเครื่อง"
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
            <SelectTrigger aria-label="กรองสถานะการเช่า" className="w-full sm:w-40">
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
            disabled={loading}
            onChange={(value) => update(value)}
          />
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

      <AdminStatGrid className="xl:grid-cols-5">
        {cards.map((card, index) => (
          <motion.div
            key={card.key}
            className="min-w-0"
            initial={reduceMotion ? false : { opacity: 0, y: 8 }}
            animate={{ opacity: 1, y: 0 }}
            transition={
              reduceMotion ? { duration: 0 } : { duration: 0.24, delay: index * 0.04 }
            }
          >
            <AdminStatCard
              label={card.label}
              value={
                data ? (
                  card.value
                ) : (
                  <SkeletonBlock className="h-7 w-20" />
                )
              }
              hint={
                data ? (
                  card.hint
                ) : (
                  <SkeletonBlock className="h-3 w-28" radius="rounded-full" />
                )
              }
              icon={card.icon}
              tone={card.tone}
            />
          </motion.div>
        ))}
      </AdminStatGrid>

      {data ? (
        <RentalCharts
          chart={data.chart}
          chartPrevious={data.chartPrevious}
          current={{ revenueSatang: stats.revenueSatang, rentalCount: stats.rentalCount }}
          previous={{ revenueSatang: previous.revenueSatang, rentalCount: previous.rentalCount }}
        />
      ) : (
        <div className="grid items-start gap-4 xl:grid-cols-2">
          <SkeletonBlock className="h-80 w-full" />
          <SkeletonBlock className="h-80 w-full" />
        </div>
      )}

      <AdminCard>
        <div className="flex flex-wrap items-center justify-between gap-2 border-b p-4">
          <div>
            <p className="font-medium">ตารางประวัติการเช่า</p>
            <p className="text-xs text-muted-foreground" role="status" aria-live="polite">
              {loading && !data ? (
                <InlineSkeleton lines={1} widths={["w-40"]} />
              ) : (
                `แสดง ${entries.length} จาก ${data?.pagination.total ?? 0} รายการ`
              )}
            </p>
          </div>
          <div className="flex flex-wrap items-center gap-1">
            <Button
              variant="ghost"
              size="sm"
              onClick={() =>
                update({
                  sort: filters.sort === "newest" ? "oldest" : "newest",
                })
              }
            >
              วันที่
              <ArrowDownUp />
            </Button>
            <Button
              variant="ghost"
              size="sm"
              onClick={() =>
                update({
                  sort: filters.sort === "amount_desc" ? "amount_asc" : "amount_desc",
                })
              }
            >
              จำนวนเงิน
              <ArrowDownUp />
            </Button>
          </div>
        </div>
        {loading && !data ? (
          <LoadingRegion label="กำลังโหลดตารางประวัติการเช่า" className="p-4">
            <TableSkeleton rows={5} columns={9} frame={false} />
          </LoadingRegion>
        ) : entries.length === 0 ? (
          <Empty className="border-0 py-10">
            <EmptyHeader>
              <EmptyMedia variant="icon">
                <History aria-hidden />
              </EmptyMedia>
              <EmptyTitle>
                {hasFilters ? "ไม่พบรายการที่ตรงกับตัวกรอง" : "ยังไม่มีประวัติการเช่า"}
              </EmptyTitle>
              <EmptyDescription>
                {hasFilters
                  ? "ลองเปลี่ยนคำค้นหาหรือช่วงเวลา"
                  : "เมื่อมีผู้เช่าเครื่อง รายการและยอดเงินจะแสดงที่นี่"}
              </EmptyDescription>
            </EmptyHeader>
          </Empty>
        ) : (
          <>
            <div className="overflow-x-auto">
              <Table>
                <caption className="sr-only">ประวัติการเช่าเครื่องทั้งหมด</caption>
                <TableHeader>
                  <TableRow>
                    <TableHead>ผู้เช่า</TableHead>
                    <TableHead>รหัส / เครื่อง</TableHead>
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
                        <TableCell className="min-w-40">
                          <span className="block truncate font-medium">{entry.userName || entry.userId}</span>
                          <span className="block truncate text-xs text-muted-foreground">{entry.userEmail || entry.userId}</span>
                        </TableCell>
                        <TableCell className="min-w-36">
                          <span className="block truncate">{entry.machineName}</span>
                          <span className="block font-mono text-xs text-muted-foreground">{entry.rentalId}</span>
                        </TableCell>
                        <TableCell className="whitespace-nowrap text-sm">
                          {entry.startsAtUtc ? formatRentalDateTime(entry.startsAtUtc) : "—"} –{" "}
                          {entry.endedAtUtc
                            ? formatRentalDateTime(entry.endedAtUtc)
                            : entry.expiresAtUtc
                              ? formatRentalDateTime(entry.expiresAtUtc)
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
                            aria-label={`ดูรายละเอียด ${entry.rentalId}`}
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
          </>
        )}
      </AdminCard>

      <RentalDetailDialog
        entry={selected}
        open={detailOpen}
        onOpenChange={(open) => {
          setDetailOpen(open);
          if (!open) setSelected(null);
        }}
        user={
          selected
            ? { userName: selected.userName, userEmail: selected.userEmail }
            : undefined
        }
      />
    </AdminPage>
  );
}
