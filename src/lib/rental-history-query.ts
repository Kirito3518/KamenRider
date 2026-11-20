/**
 * Rental history filtering/pagination shared by the customer API, the admin API,
 * and (through response contracts) the UI. Entries are merged from Controller
 * leases plus payment rows; filtering happens after that merge.
 */

import {
  resolveDateRange,
  type ResolvedDateRange,
} from "@/lib/date-range";
import type {
  AdminRentalHistoryEntry,
  AdminRentalStats,
  RentalDisplayState,
  RentalHistoryEntry,
  RentalHistoryFilters,
  RentalHistoryPagination,
  RentalHistorySort,
} from "@/lib/rental-history";
import { rentalDisplayState } from "@/lib/rental-history";

const SORT_VALUES = new Set<RentalHistorySort>([
  "newest",
  "oldest",
  "amount_desc",
  "amount_asc",
]);
const DISPLAY_STATES = new Set<RentalDisplayState>([
  "active",
  "completed",
  "expired",
  "cancelled",
  "pending",
  "unknown",
]);

type Raw = Record<string, string | undefined>;

function positiveInteger(value: string | undefined, fallback: number, max = 100) {
  const parsed = Number(value);
  if (!Number.isSafeInteger(parsed) || parsed < 1) return fallback;
  return Math.min(parsed, max);
}

export function parseRentalFilters(
  raw: Raw,
  options: { allowAll: boolean; defaultPeriod: "30d" | "all" },
  now = Date.now(),
) {
  const range: ResolvedDateRange = resolveDateRange(
    { period: raw.period, from: raw.from, to: raw.to },
    now,
    { allowAll: options.allowAll, defaultPeriod: options.defaultPeriod },
  );
  const filters: RentalHistoryFilters = {
    q: (raw.q || "").trim().slice(0, 100),
    state: DISPLAY_STATES.has(raw.state as RentalDisplayState)
      ? (raw.state as RentalDisplayState)
      : "all",
    machineId: (raw.machineId || "all").trim().slice(0, 128) || "all",
    period: range.period,
    from: range.from,
    to: range.to,
    sort: SORT_VALUES.has(raw.sort as RentalHistorySort)
      ? (raw.sort as RentalHistorySort)
      : "newest",
  };
  const page = positiveInteger(raw.page, 1, Number.MAX_SAFE_INTEGER);
  const pageSize = positiveInteger(raw.pageSize, 20, 100);
  return { filters, range, page, pageSize };
}

function createdAt(entry: RentalHistoryEntry) {
  const parsed = Date.parse(entry.createdAtUtc);
  return Number.isFinite(parsed) ? parsed : 0;
}

function amount(entry: RentalHistoryEntry) {
  return entry.netSatang ?? entry.paidSatang ?? 0;
}

export function filterRentalEntries(
  entries: RentalHistoryEntry[],
  filters: RentalHistoryFilters,
  range: ResolvedDateRange,
  options: { admin?: boolean } = {},
): RentalHistoryEntry[] {
  const needle = filters.q.toLowerCase();
  return entries.filter((entry) => {
    if (range.start !== null && range.end !== null) {
      const created = createdAt(entry);
      if (created < range.start || created >= range.end) return false;
    }
    if (filters.state !== "all" && rentalDisplayState(entry) !== filters.state) {
      return false;
    }
    if (filters.machineId !== "all" && entry.machineId !== filters.machineId) {
      return false;
    }
    if (!needle) return true;
    const values = [
      entry.rentalId,
      entry.machineId,
      entry.machineName,
    ];
    if (options.admin) {
      const admin = entry as AdminRentalHistoryEntry;
      values.push(admin.userName, admin.userEmail, admin.userId);
    }
    return values.join(" ").toLowerCase().includes(needle);
  });
}

export function sortRentalEntries(
  entries: RentalHistoryEntry[],
  sort: RentalHistorySort,
): RentalHistoryEntry[] {
  const sorted = [...entries];
  switch (sort) {
    case "oldest":
      sorted.sort((a, b) => createdAt(a) - createdAt(b));
      break;
    case "amount_desc":
      sorted.sort((a, b) => amount(b) - amount(a) || createdAt(b) - createdAt(a));
      break;
    case "amount_asc":
      sorted.sort((a, b) => amount(a) - amount(b) || createdAt(b) - createdAt(a));
      break;
    default:
      sorted.sort((a, b) => createdAt(b) - createdAt(a));
  }
  return sorted;
}

export function paginateRentalEntries<T>(
  entries: T[],
  page: number,
  pageSize: number,
): { entries: T[]; pagination: RentalHistoryPagination } {
  const pageCount = Math.max(1, Math.ceil(entries.length / pageSize));
  const safePage = Math.min(Math.max(1, page), pageCount);
  const start = (safePage - 1) * pageSize;
  return {
    entries: entries.slice(start, start + pageSize),
    pagination: {
      page: safePage,
      pageSize,
      total: entries.length,
      pageCount,
    },
  };
}

export type RentalDisplayCounts = {
  total: number;
  active: number;
  completed: number;
  expired: number;
  cancelled: number;
  pending: number;
  unknown: number;
};

export function countRentalDisplayStates(
  entries: RentalHistoryEntry[],
): RentalDisplayCounts {
  const counts: RentalDisplayCounts = {
    total: entries.length,
    active: 0,
    completed: 0,
    expired: 0,
    cancelled: 0,
    pending: 0,
    unknown: 0,
  };
  for (const entry of entries) {
    counts[rentalDisplayState(entry)] += 1;
  }
  return counts;
}

export function rentalStatsFromEntries(
  allEntries: RentalHistoryEntry[],
  rangeEntries: RentalHistoryEntry[],
  revenueSatang: number,
  rentalCount: number,
): AdminRentalStats {
  const counts = countRentalDisplayStates(rangeEntries);
  return {
    totalRentals: counts.total,
    activeRentals: countRentalDisplayStates(allEntries).active,
    completedRentals: counts.completed,
    expiredRentals: counts.expired,
    revenueSatang,
    rentalCount,
  };
}


