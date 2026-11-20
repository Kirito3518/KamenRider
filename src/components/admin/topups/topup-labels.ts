/**
 * Shared labels, icons, semantic chart colours and tiny pure helpers for the
 * Admin top-up dashboard. Kept plain TypeScript so the table, charts and dialog
 * always describe a channel/status the same way.
 */

import {
  Gift,
  Landmark,
  QrCode,
  TicketPercent,
  type LucideIcon,
} from "lucide-react";
import type { TopupChannel, TopupSort } from "@/lib/topup/constants";
import type {
  TopupChannelFilter,
  TopupEntry,
  TopupPeriod,
  TopupStatusFilter,
} from "@/lib/topup/types";

/** Admin dashboard page size is fixed server-side too (TOPUP_PAGE_SIZE). */
export const TOPUP_PAGE_SIZE = 20;

export type ChannelVisual = {
  label: string;
  icon: LucideIcon;
  /** Semantic design token, never a raw colour. */
  color: string;
};

export const CHANNEL_META: Record<TopupChannel, ChannelVisual> = {
  truemoney: { label: "TrueMoney", icon: Gift, color: "var(--primary)" },
  promptpay: { label: "PromptPay", icon: QrCode, color: "var(--status-available)" },
  bank_transfer: { label: "โอนธนาคาร", icon: Landmark, color: "var(--status-busy)" },
  coupon: { label: "คูปอง", icon: TicketPercent, color: "var(--chart-2)" },
};

export function channelLabel(channel: TopupChannel): string {
  return CHANNEL_META[channel].label;
}

export const CHANNEL_FILTER_OPTIONS: { value: TopupChannelFilter; label: string }[] = [
  { value: "all", label: "ทุกช่องทาง" },
  { value: "truemoney", label: "TrueMoney" },
  { value: "promptpay", label: "PromptPay" },
  { value: "bank_transfer", label: "โอนธนาคาร" },
  { value: "coupon", label: "คูปอง" },
];

export const STATUS_FILTER_OPTIONS: { value: TopupStatusFilter; label: string }[] = [
  { value: "all", label: "ทุกสถานะ" },
  { value: "pending", label: "รอชำระเงิน" },
  { value: "processing", label: "กำลังตรวจสอบ" },
  { value: "completed", label: "สำเร็จ" },
  { value: "failed", label: "ไม่สำเร็จ" },
  { value: "expired", label: "หมดอายุ" },
];

export const SORT_OPTIONS: { value: TopupSort; label: string }[] = [
  { value: "newest", label: "ใหม่สุดก่อน" },
  { value: "oldest", label: "เก่าสุดก่อน" },
  { value: "amount_desc", label: "จำนวนเงินมาก → น้อย" },
  { value: "amount_asc", label: "จำนวนเงินน้อย → มาก" },
];

export const PERIOD_OPTIONS: { value: Exclude<TopupPeriod, "all">; label: string }[] = [
  { value: "today", label: "วันนี้" },
  { value: "7d", label: "7 วัน" },
  { value: "30d", label: "30 วัน" },
  { value: "90d", label: "90 วัน" },
  { value: "custom", label: "กำหนดเอง" },
];

export function sortLabel(sort: TopupSort): string {
  return SORT_OPTIONS.find((option) => option.value === sort)?.label ?? SORT_OPTIONS[0].label;
}

/** Amount an entry represents: credited money when present, else the request. */
export function entryAmount(
  entry: Pick<TopupEntry, "creditedSatang" | "requestedSatang">,
): number | null {
  if (entry.creditedSatang > 0) return entry.creditedSatang;
  return entry.requestedSatang;
}

/**
 * Percent change against the previous period. `null` means there is no
 * comparison base (previous value is zero or invalid) and the UI must say so.
 */
export function percentChange(current: number, previous: number): number | null {
  if (!Number.isFinite(current) || !Number.isFinite(previous) || previous <= 0) {
    return null;
  }
  return ((current - previous) / previous) * 100;
}
