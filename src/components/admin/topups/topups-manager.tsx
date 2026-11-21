"use client";

import { useCallback, useEffect, useRef, useState, type FormEvent } from "react";
import {
  AlertTriangle,
  ChevronLeft,
  ChevronRight,
  CircleAlert,
  Eye,
  ReceiptText,
  RefreshCw,
} from "lucide-react";
import { motion, useReducedMotion } from "motion/react";
import { AdminCard, AdminPage, AdminPageHeader } from "@/components/admin/admin-page";
import { TopupCharts } from "@/components/admin/topups/topup-charts";
import { TopupDetailDialog } from "@/components/admin/topups/topup-detail-dialog";
import { TopupStats } from "@/components/admin/topups/topup-stats";
import {
  CHANNEL_FILTER_OPTIONS,
  entryAmount,
  PERIOD_OPTIONS,
  SORT_OPTIONS,
  STATUS_FILTER_OPTIONS,
  TOPUP_PAGE_SIZE,
  sortLabel,
} from "@/components/admin/topups/topup-labels";
import { Alert, AlertDescription, AlertTitle } from "@/components/ui/alert";
import { Badge } from "@/components/ui/badge";
import { Button } from "@/components/ui/button";
import {
  CardAction,
  CardContent,
  CardDescription,
  CardHeader,
  CardTitle,
} from "@/components/ui/card";
import {
  Empty,
  EmptyDescription,
  EmptyHeader,
  EmptyMedia,
  EmptyTitle,
} from "@/components/ui/empty";
import { Field, FieldGroup, FieldLabel } from "@/components/ui/field";
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
import { ToggleGroup, ToggleGroupItem } from "@/components/ui/toggle-group";
import { formatBangkokDateTime, formatRangeLabel, isValidDateKey } from "@/lib/date-range";
import { formatBalance } from "@/lib/money";
import type { TopupSort } from "@/lib/topup/constants";
import type {
  AdminTopupDashboardResponse,
  TopupChannelFilter,
  TopupFilters,
  TopupPeriod,
  TopupStatusFilter,
} from "@/lib/topup/types";
import { clientErrorMessage, topupFetch } from "@/components/topup/client";
import {
  ChannelBadge,
  StatusBadge,
  VerificationBadge,
} from "@/components/topup/topup-display";

type QueryState = TopupFilters & { page: number };

const DEFAULT_QUERY: QueryState = {
  q: "",
  channel: "all",
  status: "all",
  period: "30d",
  from: "",
  to: "",
  sort: "newest",
  page: 1,
};

function buildQueryString(query: QueryState): string {
  const params = new URLSearchParams();
  if (query.q) params.set("q", query.q);
  if (query.channel !== "all") params.set("channel", query.channel);
  if (query.status !== "all") params.set("status", query.status);
  params.set("period", query.period);
  if (query.period === "custom") {
    if (query.from) params.set("from", query.from);
    if (query.to) params.set("to", query.to);
  }
  if (query.sort !== "newest") params.set("sort", query.sort);
  if (query.page > 1) params.set("page", String(query.page));
  params.set("pageSize", String(TOPUP_PAGE_SIZE));
  return params.toString();
}

/**
 * Admin top-up dashboard: server-filtered history, channel KPIs, stacked
 * charts and an inspector with a safe admin re-verify action. All amount and
 * date formatting uses the shared Bangkok-time / satang helpers.
 */
export function TopupsManager() {
  const reduceMotion = useReducedMotion();
  const [query, setQuery] = useState<QueryState>(DEFAULT_QUERY);
  const [searchInput, setSearchInput] = useState("");
  const [data, setData] = useState<AdminTopupDashboardResponse | null>(null);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState("");
  const [rangeMode, setRangeMode] = useState<TopupPeriod>("30d");
  const [draftFrom, setDraftFrom] = useState("");
  const [draftTo, setDraftTo] = useState("");
  const [localRangeError, setLocalRangeError] = useState("");
  const [selectedId, setSelectedId] = useState<string | null>(null);
  const requestRef = useRef(0);

  const load = useCallback(async () => {
    const requestId = ++requestRef.current;
    setLoading(true);
    try {
      const payload = await topupFetch<AdminTopupDashboardResponse>(
        `/api/admin/topups?${buildQueryString(query)}`,
      );
      if (requestId !== requestRef.current) return;
      setData(payload);
      setError("");
      if (payload.filters.period !== query.period) {
        // The API fell back to a safe range (invalid custom range).
        setRangeMode(payload.filters.period);
        if (payload.filters.period === "custom") {
          setDraftFrom(payload.filters.from);
          setDraftTo(payload.filters.to);
        }
      }
    } catch (cause) {
      if (requestId !== requestRef.current) return;
      setError(clientErrorMessage(cause));
    } finally {
      if (requestId === requestRef.current) setLoading(false);
    }
  }, [query]);

  useEffect(() => {
    const timer = window.setTimeout(() => void load(), 0);
    return () => window.clearTimeout(timer);
  }, [load]);

  const updateQuery = useCallback((partial: Partial<QueryState>, resetPage = true) => {
    setQuery((current) => {
      const next = { ...current, ...partial };
      if (resetPage) next.page = 1;
      return next;
    });
  }, []);

  // Debounced search so typing does not spam the admin API.
  useEffect(() => {
    const trimmed = searchInput.trim();
    if (trimmed === query.q) return;
    const timer = window.setTimeout(() => updateQuery({ q: trimmed }), 300);
    return () => window.clearTimeout(timer);
  }, [searchInput, query.q, updateQuery]);

  function submitSearch(event: FormEvent<HTMLFormElement>) {
    event.preventDefault();
    updateQuery({ q: searchInput.trim() });
  }

  function selectPeriod(next: TopupPeriod) {
    if (next === "custom") {
      setRangeMode("custom");
      setDraftFrom((current) => current || query.from || data?.filters.from || "");
      setDraftTo((current) => current || query.to || data?.filters.to || "");
      return;
    }
    setRangeMode(next);
    setLocalRangeError("");
    updateQuery({ period: next, from: "", to: "" });
  }

  function applyCustomRange(event: FormEvent<HTMLFormElement>) {
    event.preventDefault();
    if (!isValidDateKey(draftFrom) || !isValidDateKey(draftTo) || draftFrom > draftTo) {
      setLocalRangeError("เลือกวันที่เริ่มต้นและสิ้นสุดให้ถูกต้อง");
      return;
    }
    setLocalRangeError("");
    updateQuery({ period: "custom", from: draftFrom, to: draftTo });
  }

  function resetFilters() {
    setSearchInput("");
    setRangeMode("30d");
    setLocalRangeError("");
    setDraftFrom("");
    setDraftTo("");
    setQuery({ ...DEFAULT_QUERY });
  }

  const entries = data?.entries ?? [];
  const pagination = data?.pagination;
  const total = pagination?.total ?? 0;
  const page = pagination?.page ?? 1;
  const pageCount = pagination?.pageCount ?? 1;
  const pageSize = pagination?.pageSize ?? TOPUP_PAGE_SIZE;
  const shownFrom = total === 0 ? 0 : (page - 1) * pageSize + 1;
  const shownTo = total === 0 ? 0 : Math.min(page * pageSize, total);
  const rangeLabel = data ? formatRangeLabel(data.filters.from, data.filters.to) : "—";
  const rangeError = localRangeError || data?.rangeError || "";
  const hasFilters =
    Boolean(query.q) ||
    query.channel !== "all" ||
    query.status !== "all" ||
    query.period !== "30d" ||
    query.sort !== "newest";

  return (
    <AdminPage aria-busy={loading}>
      <motion.div
        initial={reduceMotion ? false : { opacity: 0, y: 8 }}
        animate={{ opacity: 1, y: 0 }}
        transition={reduceMotion ? { duration: 0 } : { duration: 0.25 }}
      >
        <AdminPageHeader
          icon={ReceiptText}
          title="ประวัติการเติมเงิน"
          description="รายการเติมเงินทั้งหมดของผู้ใช้ พร้อมยอดเงิน ผลตรวจสลิป และประวัติการเรียกผู้ให้บริการ · เวลาไทย (Asia/Bangkok)"
          actions={
            <>
              <RangeSelector value={rangeMode} onChange={selectPeriod} />
              <Button
                type="button"
                variant="outline"
                size="sm"
                disabled={loading}
                onClick={() => void load()}
              >
                {loading ? (
                  <Spinner data-icon="inline-start" />
                ) : (
                  <RefreshCw data-icon="inline-start" />
                )}
                รีเฟรช
              </Button>
            </>
          }
        />
      </motion.div>

      {rangeMode === "custom" ? (
        <motion.form
          onSubmit={applyCustomRange}
          initial={reduceMotion ? false : { opacity: 0, y: -6 }}
          animate={{ opacity: 1, y: 0 }}
          transition={reduceMotion ? { duration: 0 } : { duration: 0.2 }}
          className="rounded-2xl border bg-card p-4"
          aria-label="เลือกช่วงเวลากำหนดเอง"
        >
          <FieldGroup className="grid gap-3 sm:grid-cols-2 xl:max-w-2xl xl:grid-cols-[1fr_1fr_auto]">
            <Field>
              <FieldLabel htmlFor="admin-topup-range-from">ตั้งแต่วันที่</FieldLabel>
              <Input
                id="admin-topup-range-from"
                type="date"
                value={draftFrom}
                max={draftTo || undefined}
                onChange={(event) => setDraftFrom(event.currentTarget.value)}
              />
            </Field>
            <Field>
              <FieldLabel htmlFor="admin-topup-range-to">ถึงวันที่</FieldLabel>
              <Input
                id="admin-topup-range-to"
                type="date"
                value={draftTo}
                min={draftFrom || undefined}
                onChange={(event) => setDraftTo(event.currentTarget.value)}
              />
            </Field>
            <Button type="submit">ใช้ช่วงเวลา</Button>
          </FieldGroup>
        </motion.form>
      ) : null}

      {rangeError ? (
        <Alert variant="destructive">
          <AlertTriangle aria-hidden />
          <AlertTitle>ช่วงเวลาไม่ถูกต้อง</AlertTitle>
          <AlertDescription>{rangeError}</AlertDescription>
        </Alert>
      ) : null}

      {error ? (
        <Alert variant="destructive">
          <CircleAlert aria-hidden />
          <AlertTitle>โหลดประวัติการเติมเงินไม่สำเร็จ</AlertTitle>
          <AlertDescription>
            <p>{error}</p>
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

      {!data ? (
        loading ? (
          <DashboardSkeleton />
        ) : null
      ) : (
        <>
          <TopupStats stats={data.stats} previous={data.previous} />
          <TopupCharts chart={data.chart} previous={data.chartPrevious} />

          <motion.div
            initial={reduceMotion ? false : { opacity: 0, y: 8 }}
            animate={{ opacity: 1, y: 0 }}
            transition={reduceMotion ? { duration: 0 } : { duration: 0.25, delay: 0.16 }}
          >
            <AdminCard className="gap-0">
              <CardHeader className="gap-1 border-b pb-4">
                <CardTitle>รายการเติมเงิน</CardTitle>
                <CardDescription>
                  {`ช่วงข้อมูล ${rangeLabel} · เรียงตาม${sortLabel(query.sort)} · เวลาไทย`}
                </CardDescription>
                <CardAction>
                  <span className="text-xs text-muted-foreground">
                    {loading ? "กำลังอัปเดต…" : `${total.toLocaleString("th-TH")} รายการ`}
                  </span>
                </CardAction>
              </CardHeader>

              <CardContent className="flex flex-col gap-3 pt-4">
                <div className="flex flex-col gap-3 xl:flex-row xl:items-center">
                  <form
                    role="search"
                    className="w-full xl:max-w-sm"
                    onSubmit={submitSearch}
                  >
                    <Input
                      type="search"
                      value={searchInput}
                      onChange={(event) => setSearchInput(event.currentTarget.value)}
                      placeholder="ค้นหาชื่อผู้ใช้ อีเมล หรือเลขอ้างอิง"
                      aria-label="ค้นหาผู้ใช้ อีเมล หรือเลขอ้างอิง"
                      maxLength={100}
                    />
                  </form>
                  <div className="grid gap-2 sm:grid-cols-3 xl:flex xl:flex-wrap">
                    <Select
                      items={CHANNEL_FILTER_OPTIONS}
                      value={query.channel}
                      onValueChange={(value) =>
                        updateQuery({ channel: (value as TopupChannelFilter) ?? "all" })
                      }
                    >
                      <SelectTrigger
                        aria-label="กรองช่องทางเติมเงิน"
                        className="w-full xl:w-40"
                      >
                        <SelectValue />
                      </SelectTrigger>
                      <SelectContent>
                        <SelectGroup>
                          {CHANNEL_FILTER_OPTIONS.map((option) => (
                            <SelectItem key={option.value} value={option.value}>
                              {option.label}
                            </SelectItem>
                          ))}
                        </SelectGroup>
                      </SelectContent>
                    </Select>
                    <Select
                      items={STATUS_FILTER_OPTIONS}
                      value={query.status}
                      onValueChange={(value) =>
                        updateQuery({ status: (value as TopupStatusFilter) ?? "all" })
                      }
                    >
                      <SelectTrigger
                        aria-label="กรองสถานะการเติมเงิน"
                        className="w-full xl:w-40"
                      >
                        <SelectValue />
                      </SelectTrigger>
                      <SelectContent>
                        <SelectGroup>
                          {STATUS_FILTER_OPTIONS.map((option) => (
                            <SelectItem key={option.value} value={option.value}>
                              {option.label}
                            </SelectItem>
                          ))}
                        </SelectGroup>
                      </SelectContent>
                    </Select>
                    <Select
                      items={SORT_OPTIONS}
                      value={query.sort}
                      onValueChange={(value) =>
                        updateQuery({ sort: (value as TopupSort) ?? "newest" })
                      }
                    >
                      <SelectTrigger aria-label="เรียงลำดับรายการ" className="w-full xl:w-44">
                        <SelectValue />
                      </SelectTrigger>
                      <SelectContent>
                        <SelectGroup>
                          {SORT_OPTIONS.map((option) => (
                            <SelectItem key={option.value} value={option.value}>
                              {option.label}
                            </SelectItem>
                          ))}
                        </SelectGroup>
                      </SelectContent>
                    </Select>
                  </div>
                  {hasFilters ? (
                    <Button
                      type="button"
                      variant="ghost"
                      size="sm"
                      onClick={resetFilters}
                    >
                      ล้างตัวกรอง
                    </Button>
                  ) : null}
                </div>
              </CardContent>

              {entries.length === 0 ? (
                <div className="border-t">
                  <Empty className="border-0 py-10">
                    <EmptyHeader>
                      <EmptyMedia variant="icon">
                        <ReceiptText aria-hidden />
                      </EmptyMedia>
                      <EmptyTitle>
                        {hasFilters
                          ? "ไม่พบรายการที่ตรงกับตัวกรอง"
                          : "ยังไม่มีรายการเติมเงิน"}
                      </EmptyTitle>
                      <EmptyDescription>
                        {hasFilters
                          ? "ลองเปลี่ยนคำค้นหาช่วงเวลาหรือตัวกรองอื่น"
                          : "เมื่อมีผู้ใช้เติมเงิน รายการและผลตรวจสลิปจะแสดงที่นี่"}
                      </EmptyDescription>
                    </EmptyHeader>
                    {hasFilters ? (
                      <Button
                        type="button"
                        variant="outline"
                        size="sm"
                        onClick={resetFilters}
                      >
                        ล้างตัวกรอง
                      </Button>
                    ) : null}
                  </Empty>
                </div>
              ) : (
                <>
                  <Table>
                    <caption className="sr-only">
                      รายการเติมเงินของผู้ใช้ พร้อมสถานะและผลตรวจสลิป
                    </caption>
                    <TableHeader>
                      <TableRow>
                        <TableHead>ผู้ใช้</TableHead>
                        <TableHead>เลขอ้างอิง</TableHead>
                        <TableHead>ช่องทาง</TableHead>
                        <TableHead className="text-right">จำนวนเงิน</TableHead>
                        <TableHead>วันที่</TableHead>
                        <TableHead>สถานะ</TableHead>
                        <TableHead>ผลตรวจสลิป</TableHead>
                        <TableHead className="text-right">จัดการ</TableHead>
                      </TableRow>
                    </TableHeader>
                    <TableBody>
                      {entries.map((entry) => {
                        const amount = entryAmount(entry);
                        return (
                          <TableRow key={entry.id}>
                            <TableCell className="min-w-44">
                              <div className="flex flex-col gap-0.5">
                                <span className="truncate font-medium">
                                  {entry.userName || entry.userId}
                                </span>
                                <span className="truncate text-xs text-muted-foreground">
                                  {entry.userEmail || entry.userId}
                                </span>
                              </div>
                            </TableCell>
                            <TableCell className="font-mono text-xs">
                              <span
                                className="block max-w-40 truncate"
                                title={entry.reference}
                              >
                                {entry.reference}
                              </span>
                            </TableCell>
                            <TableCell>
                              <ChannelBadge channel={entry.channel} />
                            </TableCell>
                            <TableCell className="text-right font-medium tabular-nums">
                              {amount === null ? "—" : formatBalance(amount)}
                            </TableCell>
                            <TableCell className="whitespace-nowrap text-sm">
                              {formatBangkokDateTime(entry.createdAt)}
                            </TableCell>
                            <TableCell>
                              <StatusBadge status={entry.status} />
                            </TableCell>
                            <TableCell>
                              <div className="flex flex-wrap items-center gap-1">
                                <VerificationBadge status={entry.verificationStatus} />
                                {entry.canReview ? (
                                  <Badge variant="busy">รอตรวจสอบ</Badge>
                                ) : null}
                              </div>
                            </TableCell>
                            <TableCell className="text-right">
                              <Button
                                type="button"
                                variant="outline"
                                size="icon-sm"
                                aria-label={`ดูรายละเอียด ${entry.reference}`}
                                title="ดูรายละเอียด"
                                onClick={() => setSelectedId(entry.id)}
                              >
                                <Eye />
                              </Button>
                            </TableCell>
                          </TableRow>
                        );
                      })}
                    </TableBody>
                  </Table>

                  <div className="flex flex-wrap items-center justify-between gap-3 border-t p-3 text-xs text-muted-foreground">
                    <p>
                      {`แสดง ${shownFrom}–${shownTo} จาก ${total.toLocaleString("th-TH")} รายการ`}
                    </p>
                    <div className="flex items-center gap-2">
                      <span>{`หน้า ${page} / ${pageCount}`}</span>
                      <Button
                        type="button"
                        variant="outline"
                        size="icon-sm"
                        aria-label="หน้าก่อนหน้า"
                        disabled={loading || page <= 1}
                        onClick={() => updateQuery({ page: page - 1 }, false)}
                      >
                        <ChevronLeft />
                      </Button>
                      <Button
                        type="button"
                        variant="outline"
                        size="icon-sm"
                        aria-label="หน้าถัดไป"
                        disabled={loading || page >= pageCount}
                        onClick={() => updateQuery({ page: page + 1 }, false)}
                      >
                        <ChevronRight />
                      </Button>
                    </div>
                  </div>
                </>
              )}
            </AdminCard>
          </motion.div>
        </>
      )}

      <TopupDetailDialog
        key={selectedId ?? "closed"}
        topupId={selectedId}
        onClose={() => setSelectedId(null)}
      />

      <p role="status" aria-live="polite" className="sr-only">
        {loading ? "กำลังโหลดประวัติการเติมเงิน…" : error || ""}
      </p>
    </AdminPage>
  );
}

function RangeSelector({
  value,
  onChange,
}: {
  value: TopupPeriod;
  onChange: (value: TopupPeriod) => void;
}) {
  return (
    <div className="max-w-full overflow-x-auto">
      <ToggleGroup
        value={[value]}
        onValueChange={(values) => {
          const next = values[0] as TopupPeriod | undefined;
          if (next) onChange(next);
        }}
        aria-label="ช่วงเวลาของรายการเติมเงิน"
        spacing={1}
        className="rounded-xl border bg-muted/50 p-1"
      >
        {PERIOD_OPTIONS.map((option) => (
          <ToggleGroupItem
            key={option.value}
            value={option.value}
            size="sm"
            className="rounded-lg px-3 text-xs data-pressed:bg-primary data-pressed:text-primary-foreground"
          >
            {option.label}
          </ToggleGroupItem>
        ))}
      </ToggleGroup>
    </div>
  );
}

function DashboardSkeleton() {
  return (
    <div className="flex flex-col gap-4" aria-hidden>
      <div className="grid gap-3 sm:grid-cols-2 lg:grid-cols-3 2xl:grid-cols-6">
        {Array.from({ length: 6 }).map((_, index) => (
          <Skeleton key={index} className="h-28 w-full" />
        ))}
      </div>
      <div className="grid gap-3 xl:grid-cols-2">
        <Skeleton className="h-80 w-full" />
        <Skeleton className="h-80 w-full" />
      </div>
      <div className="flex flex-col gap-2 rounded-2xl border bg-card p-4">
        <Skeleton className="h-10 w-full" />
        {Array.from({ length: 5 }).map((_, index) => (
          <Skeleton key={index} className="h-12 w-full" />
        ))}
      </div>
    </div>
  );
}
