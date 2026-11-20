/**
 * Bangkok-time date-range helpers shared by the top-up and rental history
 * screens. Thailand has no DST, so a fixed +07:00 offset is exact.
 */

import type { TopupPeriod } from "@/lib/topup/types";

export const BANGKOK_OFFSET_MS = 7 * 60 * 60 * 1_000;
export const DAY_MS = 24 * 60 * 60 * 1_000;

export type HistoryBucket = "hour" | "day" | "month";

export type ResolvedDateRange = {
  period: TopupPeriod;
  from: string;
  to: string;
  /** Inclusive start (Bangkok midnight) in epoch ms; null for "all". */
  start: number | null;
  /** Exclusive end in epoch ms; null for "all". */
  end: number | null;
  previousStart: number | null;
  previousEnd: number | null;
  bucket: HistoryBucket;
  label: string;
  error: string;
  dayCount: number;
  isAll: boolean;
};

function bangkokIso(ms: number) {
  return new Date(ms + BANGKOK_OFFSET_MS).toISOString();
}

export function bangkokDateKey(ms: number): string {
  return bangkokIso(ms).slice(0, 10);
}

export function bangkokMidnight(dateKey: string): number {
  return Date.parse(`${dateKey}T00:00:00+07:00`);
}

export function isValidDateKey(value: string): boolean {
  if (!/^\d{4}-\d{2}-\d{2}$/.test(value)) return false;
  const midnight = bangkokMidnight(value);
  return Number.isFinite(midnight) && bangkokDateKey(midnight) === value;
}

const shortDate = new Intl.DateTimeFormat("th-TH", {
  timeZone: "Asia/Bangkok",
  day: "numeric",
  month: "short",
});

const longDate = new Intl.DateTimeFormat("th-TH", {
  timeZone: "Asia/Bangkok",
  day: "numeric",
  month: "short",
  year: "numeric",
});

const monthLabel = new Intl.DateTimeFormat("th-TH", {
  timeZone: "Asia/Bangkok",
  month: "short",
});

const dateTime = new Intl.DateTimeFormat("th-TH", {
  timeZone: "Asia/Bangkok",
  dateStyle: "medium",
  timeStyle: "short",
});

export function formatBangkokDate(ms: number | null | undefined): string {
  if (ms === null || ms === undefined || !Number.isFinite(ms)) return "—";
  return longDate.format(ms);
}

export function formatBangkokShortDate(ms: number): string {
  return shortDate.format(ms);
}

export function formatBangkokDateTime(ms: number | null | undefined): string {
  if (ms === null || ms === undefined || !Number.isFinite(ms)) return "—";
  return dateTime.format(ms);
}

export function formatRangeLabel(from: string, to: string): string {
  if (!from || !to) return "ทั้งหมด";
  if (from === to) return longDate.format(bangkokMidnight(from));
  return `${shortDate.format(bangkokMidnight(from))} – ${shortDate.format(bangkokMidnight(to))}`;
}

function periodDays(period: TopupPeriod): number {
  switch (period) {
    case "today":
      return 1;
    case "7d":
      return 7;
    case "30d":
      return 30;
    case "90d":
      return 90;
    default:
      return 30;
  }
}

export type ResolveDateRangeOptions = {
  allowAll?: boolean;
  defaultPeriod?: TopupPeriod;
};

/**
 * Resolves UI period/from/to into a Bangkok date range. Invalid custom ranges
 * return a readable Thai error and fall back to the default 30-day window.
 */
export function resolveDateRange(
  raw: { period?: string | null; from?: string | null; to?: string | null },
  now = Date.now(),
  options: ResolveDateRangeOptions = {},
): ResolvedDateRange {
  const allowAll = options.allowAll === true;
  const defaultPeriod: TopupPeriod =
    options.defaultPeriod ?? (allowAll ? "all" : "30d");
  const requested = raw.period;
  const allowed: TopupPeriod[] = ["today", "7d", "30d", "90d", "custom"];
  if (allowAll) allowed.push("all");
  let period: TopupPeriod = allowed.includes(requested as TopupPeriod)
    ? (requested as TopupPeriod)
    : defaultPeriod;

  if (period === "all") {
    return {
      period,
      from: "",
      to: "",
      start: null,
      end: null,
      previousStart: null,
      previousEnd: null,
      bucket: "month",
      label: "ทั้งหมด",
      error: "",
      dayCount: 0,
      isAll: true,
    };
  }

  let error = "";
  let from: string;
  let to: string;
  const today = bangkokDateKey(now);

  if (period === "custom") {
    const rawFrom = (raw.from || "").trim();
    const rawTo = (raw.to || "").trim();
    if (!isValidDateKey(rawFrom) || !isValidDateKey(rawTo) || rawFrom > rawTo) {
      error = "เลือกวันที่เริ่มต้นและสิ้นสุดให้ถูกต้อง";
      period = defaultPeriod === "all" ? "30d" : defaultPeriod;
      const days = periodDays(period);
      from = bangkokDateKey(now - (days - 1) * DAY_MS);
      to = today;
    } else {
      const midnightFrom = bangkokMidnight(rawFrom);
      const midnightTo = bangkokMidnight(rawTo);
      if (midnightTo - midnightFrom >= 366 * DAY_MS) {
        error = "ช่วงเวลาต้องไม่เกิน 366 วัน";
        from = bangkokDateKey(now - 29 * DAY_MS);
        to = today;
      } else {
        from = rawFrom;
        to = rawTo;
      }
    }
  } else {
    const days = periodDays(period);
    from = bangkokDateKey(now - (days - 1) * DAY_MS);
    to = today;
  }

  const start = bangkokMidnight(from);
  const end = bangkokMidnight(to) + DAY_MS;
  const dayCount = Math.max(1, Math.round((end - start) / DAY_MS));
  const bucket: HistoryBucket =
    period === "today"
      ? "hour"
      : dayCount > 62
        ? "month"
        : "day";
  return {
    period,
    from,
    to,
    start,
    end,
    previousStart: start - (end - start),
    previousEnd: start,
    bucket,
    label:
      period === "today"
        ? `วันนี้ (${formatBangkokShortDate(start)})`
        : formatRangeLabel(from, to),
    error,
    dayCount,
    isAll: false,
  };
}

/** SQLite strftime-compatible bucket key in Bangkok time. */
export function rangeBucketKey(ms: number, bucket: HistoryBucket): string {
  const iso = bangkokIso(ms);
  if (bucket === "hour") return iso.slice(11, 13);
  if (bucket === "month") return iso.slice(0, 7);
  return iso.slice(0, 10);
}

export function rangeBucketLabel(key: string, bucket: HistoryBucket): string {
  if (bucket === "hour") return `${key}:00`;
  if (bucket === "month") {
    const [year, month] = key.split("-").map(Number);
    const date = new Date(Date.UTC(year, (month || 1) - 1, 1) - BANGKOK_OFFSET_MS);
    return monthLabel.format(date);
  }
  return shortDate.format(bangkokMidnight(key));
}

/** Builds every bucket in a range so charts render zero-value periods too. */
export function enumerateRangeBuckets(
  start: number,
  end: number,
  bucket: HistoryBucket,
): { key: string; label: string }[] {
  const buckets: { key: string; label: string }[] = [];
  if (!Number.isFinite(start) || !Number.isFinite(end) || end <= start) return buckets;

  if (bucket === "hour") {
    for (let hour = 0; hour < 24; hour += 1) {
      const key = String(hour).padStart(2, "0");
      buckets.push({ key, label: `${key}:00` });
    }
    return buckets;
  }

  if (bucket === "month") {
    let cursor = bangkokMidnight(`${bangkokDateKey(start).slice(0, 7)}-01`);
    while (cursor < end) {
      const key = bangkokDateKey(cursor).slice(0, 7);
      buckets.push({ key, label: rangeBucketLabel(key, "month") });
      const date = new Date(cursor + BANGKOK_OFFSET_MS);
      const nextMonth = Date.UTC(date.getUTCFullYear(), date.getUTCMonth() + 1, 1);
      cursor = nextMonth - BANGKOK_OFFSET_MS;
    }
    return buckets;
  }

  let cursor = start;
  while (cursor < end) {
    const key = bangkokDateKey(cursor);
    buckets.push({ key, label: rangeBucketLabel(key, "day") });
    cursor += DAY_MS;
  }
  return buckets;
}

export function nextDayBangkokFromDateKey(dateKey: string): number {
  return bangkokMidnight(dateKey) + DAY_MS;
}
