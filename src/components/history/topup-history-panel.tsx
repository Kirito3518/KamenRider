"use client";

import { useCallback, useEffect, useState, type FormEvent } from "react";
import Link from "next/link";
import {
  ChevronLeft,
  ChevronRight,
  CircleAlert,
  Eye,
  RefreshCw,
  Search,
  Wallet,
} from "lucide-react";
import { Alert, AlertDescription, AlertTitle } from "@/components/ui/alert";
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
  LoadingRegion,
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
import { formatBangkokDateTime } from "@/lib/date-range";
import type {
  TopupChannelFilter,
  TopupEntry,
  TopupHistoryResponse,
  TopupStatusFilter,
} from "@/lib/topup/types";
import {
  ChannelBadge,
  StatusBadge,
  VerificationBadge,
} from "@/components/topup/topup-display";
import {
  HistoryRangePicker,
  type HistoryRangePeriod,
} from "@/components/history/history-range-picker";
import { TopupDetailDialog } from "@/components/history/topup-detail-dialog";
import {
  clientErrorMessage,
  topupFetch,
} from "@/components/topup/client";

const channelOptions: { value: TopupChannelFilter; label: string }[] = [
  { value: "all", label: "ทุกช่องทาง" },
  { value: "truemoney", label: "TrueMoney" },
  { value: "promptpay", label: "PromptPay QR" },
  { value: "bank_transfer", label: "โอนผ่านธนาคาร" },
  { value: "coupon", label: "คูปอง" },
];

const statusOptions: { value: TopupStatusFilter; label: string }[] = [
  { value: "all", label: "ทุกสถานะ" },
  { value: "pending", label: "รอชำระเงิน" },
  { value: "processing", label: "กำลังตรวจสอบ" },
  { value: "completed", label: "สำเร็จ" },
  { value: "failed", label: "ไม่สำเร็จ" },
  { value: "expired", label: "หมดอายุ" },
];

function amountFor(entry: TopupEntry) {
  return entry.creditedSatang > 0 ? entry.creditedSatang : entry.requestedSatang;
}

export function TopupHistoryPanel({
  onBalance,
}: {
  onBalance?: (balanceSatang: number) => void;
}) {
  const [data, setData] = useState<TopupHistoryResponse | null>(null);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState("");
  const [queryInput, setQueryInput] = useState("");
  const [filters, setFilters] = useState({
    q: "",
    channel: "all" as TopupChannelFilter,
    status: "all" as TopupStatusFilter,
    period: "all" as HistoryRangePeriod,
    from: "",
    to: "",
    sort: "newest" as "newest" | "oldest" | "amount_desc" | "amount_asc",
    page: 1,
  });
  const [selectedId, setSelectedId] = useState<string | null>(null);
  const [detailOpen, setDetailOpen] = useState(false);

  const load = useCallback(async (signal?: AbortSignal) => {
    setLoading(true);
    try {
      const params = new URLSearchParams();
      if (filters.q) params.set("q", filters.q);
      if (filters.channel !== "all") params.set("channel", filters.channel);
      if (filters.status !== "all") params.set("status", filters.status);
      params.set("period", filters.period);
      if (filters.period === "custom") {
        if (filters.from) params.set("from", filters.from);
        if (filters.to) params.set("to", filters.to);
      }
      params.set("sort", filters.sort);
      params.set("page", String(filters.page));
      const response = await topupFetch<TopupHistoryResponse>(
        `/api/topups?${params.toString()}`,
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
  const hasFilters =
    Boolean(filters.q) ||
    filters.channel !== "all" ||
    filters.status !== "all" ||
    filters.period !== "all";

  return (
    <div className="flex flex-col gap-4">
      <div className="flex flex-col gap-3 rounded-2xl border bg-card p-4">
        <div className="flex flex-wrap items-center gap-2">
          <form onSubmit={submitSearch} role="search" className="flex w-full max-w-sm items-center gap-2">
            <Input
              type="search"
              value={queryInput}
              onChange={(event) => setQueryInput(event.currentTarget.value)}
              placeholder="ค้นหาเลขอ้างอิง"
              aria-label="ค้นหาประวัติการเติมเงิน"
              maxLength={100}
            />
            <Button type="submit" variant="outline" size="icon-sm" aria-label="ค้นหา">
              <Search />
            </Button>
          </form>
          <Select
            items={channelOptions}
            value={filters.channel}
            onValueChange={(value) =>
              update({ channel: (value as TopupChannelFilter) ?? "all" })
            }
          >
            <SelectTrigger aria-label="กรองช่องทางเติมเงิน" className="w-full sm:w-44">
              <SelectValue />
            </SelectTrigger>
            <SelectContent>
              <SelectGroup>
                {channelOptions.map((option) => (
                  <SelectItem key={option.value} value={option.value}>
                    {option.label}
                  </SelectItem>
                ))}
              </SelectGroup>
            </SelectContent>
          </Select>
          <Select
            items={statusOptions}
            value={filters.status}
            onValueChange={(value) =>
              update({ status: (value as TopupStatusFilter) ?? "all" })
            }
          >
            <SelectTrigger aria-label="กรองสถานะการเติมเงิน" className="w-full sm:w-40">
              <SelectValue />
            </SelectTrigger>
            <SelectContent>
              <SelectGroup>
                {statusOptions.map((option) => (
                  <SelectItem key={option.value} value={option.value}>
                    {option.label}
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
          <Button
            variant="outline"
            size="sm"
            onClick={() => void load()}
            disabled={loading}
          >
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
                  channel: "all",
                  status: "all",
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
        <LoadingRegion label="กำลังโหลดประวัติการเติมเงิน">
          <TableSkeleton rows={5} columns={7} footer />
        </LoadingRegion>
      ) : error ? (
        <Alert variant="destructive">
          <CircleAlert aria-hidden />
          <AlertTitle>โหลดประวัติการเติมเงินไม่สำเร็จ</AlertTitle>
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
              <Wallet aria-hidden />
            </EmptyMedia>
            <EmptyTitle>
              {hasFilters ? "ไม่พบรายการที่ตรงกับตัวกรอง" : "ยังไม่มีประวัติการเติมเงิน"}
            </EmptyTitle>
            <EmptyDescription>
              {hasFilters
                ? "ลองเปลี่ยนคำค้นหาหรือช่วงเวลา"
                : "เมื่อคุณเติมเงิน รายการจะแสดงที่นี่"}
            </EmptyDescription>
          </EmptyHeader>
          {!hasFilters ? (
            <Button nativeButton={false} render={<Link href="/topup" />}>
              <Wallet data-icon="inline-start" />
              ไปหน้าเติมเงิน
            </Button>
          ) : null}
        </Empty>
      ) : (
        <div className="rounded-2xl border bg-card">
          <div className="overflow-x-auto">
            <Table>
              <caption className="sr-only">ประวัติการเติมเงินของคุณ</caption>
              <TableHeader>
                <TableRow>
                  <TableHead>เลขอ้างอิง</TableHead>
                  <TableHead>ช่องทาง</TableHead>
                  <TableHead className="text-right">จำนวนเงิน</TableHead>
                  <TableHead>วันที่</TableHead>
                  <TableHead>สถานะ</TableHead>
                  <TableHead>ผลตรวจสลิป</TableHead>
                  <TableHead className="text-right">รายละเอียด</TableHead>
                </TableRow>
              </TableHeader>
              <TableBody>
                {entries.map((entry) => {
                  const amount = amountFor(entry);
                  return (
                    <TableRow key={entry.id}>
                      <TableCell className="font-mono text-xs">
                        <span className="block max-w-36 truncate" title={entry.reference}>
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
                        <VerificationBadge status={entry.verificationStatus} />
                      </TableCell>
                      <TableCell className="text-right">
                        <Button
                          variant="outline"
                          size="icon-sm"
                          aria-label={`ดูรายละเอียด ${entry.reference}`}
                          title="ดูรายละเอียด"
                          onClick={() => {
                            setSelectedId(entry.id);
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
                  setFilters((current) => ({
                    ...current,
                    page: current.page + 1,
                  }))
                }
                disabled={
                  loading ||
                  (data ? data.pagination.page >= data.pagination.pageCount : true)
                }
              >
                ถัดไป
                <ChevronRight data-icon="inline-end" />
              </Button>
            </div>
          </div>
        </div>
      )}

      <TopupDetailDialog
        topupId={selectedId}
        open={detailOpen}
        onOpenChange={(open) => {
          setDetailOpen(open);
          if (!open) setSelectedId(null);
        }}
      />
    </div>
  );
}
