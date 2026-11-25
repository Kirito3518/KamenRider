/**
 * Top-up history queries for the customer and admin screens.
 *
 * All filters/sorts are allow-listed and parameterized. Customer queries always
 * include the session user id; no route accepts a userId from the browser.
 */

import type Database from "better-sqlite3";
import {
  isTopupChannel,
  isTopupSort,
  isTopupStatus,
  TOPUP_MAX_PAGE_SIZE,
  TOPUP_PAGE_SIZE,
  type TopupSort,
} from "@/lib/topup/constants";
import {
  enumerateRangeBuckets,
  resolveDateRange,
  type ResolvedDateRange,
} from "@/lib/date-range";
import { TopupError } from "@/lib/topup/errors";
import { ensureTopupSchema } from "@/lib/topup/schema";
import {
  getTopupAttempts,
  getTopupDetail,
  requireTopupRow,
  topupDetailFromRow,
  topupEntryFromRow,
  type TopupRow,
} from "@/lib/topup/transactions";
import { compareReview, isReviewableTopup } from "@/lib/topup/services/review-service";
import type {
  AdminTopupDashboardResponse,
  AdminTopupDetail,
  AdminTopupEntry,
  AdminTopupStats,
  TopupChartPoint,
  TopupFilters,
  TopupHistoryResponse,
  TopupPagination,
  TopupSummary,
} from "@/lib/topup/types";

type RawFilters = Record<string, string | undefined>;

type AdminTopupRow = TopupRow & {
  user_display_name: string;
  user_display_email: string;
};

function positiveInteger(value: string | undefined, fallback: number, max: number) {
  const parsed = Number(value);
  if (!Number.isSafeInteger(parsed) || parsed < 1) return fallback;
  return Math.min(parsed, max);
}

function escapeLike(value: string) {
  return `%${value.replace(/[\\%_]/g, "\\$&")}%`;
}

export function parseTopupFilters(
  raw: RawFilters,
  options: { allowAll: boolean; defaultPeriod: "30d" | "all" },
) {
  const range = resolveDateRange(
    { period: raw.period, from: raw.from, to: raw.to },
    Date.now(),
    { allowAll: options.allowAll, defaultPeriod: options.defaultPeriod },
  );
  const page = positiveInteger(raw.page, 1, Number.MAX_SAFE_INTEGER);
  const pageSize = positiveInteger(raw.pageSize, TOPUP_PAGE_SIZE, TOPUP_MAX_PAGE_SIZE);
  const filters: TopupFilters = {
    q: (raw.q || "").trim().slice(0, 100),
    channel: isTopupChannel(raw.channel) ? raw.channel : "all",
    status: isTopupStatus(raw.status) ? raw.status : "all",
    period: range.period,
    from: range.from,
    to: range.to,
    sort: isTopupSort(raw.sort) ? raw.sort : "newest",
  };
  return { filters, range, page, pageSize };
}

type WhereClause = { sql: string; values: (string | number)[] };

function buildWhere(
  filters: TopupFilters,
  range: ResolvedDateRange,
  options: { userId?: string; admin?: boolean },
): WhereClause {
  const clauses: string[] = [];
  const values: (string | number)[] = [];
  if (options.userId) {
    clauses.push("t.user_id = ?");
    values.push(options.userId);
  }
  if (range.start !== null && range.end !== null) {
    clauses.push("t.created_at >= ? AND t.created_at < ?");
    values.push(range.start, range.end);
  }
  if (filters.channel !== "all") {
    clauses.push("t.channel = ?");
    values.push(filters.channel);
  }
  if (filters.status !== "all") {
    clauses.push("t.status = ?");
    values.push(filters.status);
  }
  if (filters.q) {
    const needle = escapeLike(filters.q);
    const searchTerms = [
      "t.reference LIKE ? ESCAPE '\\'",
      "t.id LIKE ? ESCAPE '\\'",
      "t.slip_trans_ref LIKE ? ESCAPE '\\'",
    ];
    values.push(needle, needle, needle);
    if (options.admin) {
      searchTerms.push(
        "COALESCE(u.name, t.user_name) LIKE ? ESCAPE '\\'",
        "COALESCE(u.email, t.user_email) LIKE ? ESCAPE '\\'",
      );
      values.push(needle, needle);
    }
    clauses.push(`(${searchTerms.join(" OR ")})`);
  }
  return {
    sql: clauses.length ? `WHERE ${clauses.join(" AND ")}` : "",
    values,
  };
}

function orderBy(sort: TopupSort) {
  switch (sort) {
    case "oldest":
      return "t.created_at ASC, t.rowid ASC";
    case "amount_desc":
      return "COALESCE(t.credited_satang, t.requested_satang, 0) DESC, t.created_at DESC";
    case "amount_asc":
      return "COALESCE(t.credited_satang, t.requested_satang, 0) ASC, t.created_at DESC";
    default:
      return "t.created_at DESC, t.rowid DESC";
  }
}

function paginationFor(page: number, pageSize: number, total: number): TopupPagination {
  const pageCount = Math.max(1, Math.ceil(total / pageSize));
  return {
    page: Math.min(Math.max(1, page), pageCount),
    pageSize,
    total,
    pageCount,
  };
}

function summaryFor(database: Database.Database, where: WhereClause): TopupSummary {
  const row = database
    .prepare(
      `SELECT
         count(*) AS total,
         COALESCE(SUM(CASE WHEN t.status = 'completed' THEN 1 ELSE 0 END), 0) AS completed,
         COALESCE(SUM(CASE WHEN t.status IN ('pending','processing') THEN 1 ELSE 0 END), 0) AS pending,
         COALESCE(SUM(CASE WHEN t.status IN ('failed','expired') THEN 1 ELSE 0 END), 0) AS failed,
         COALESCE(SUM(CASE WHEN t.status = 'completed' THEN t.credited_satang ELSE 0 END), 0) AS completedSatang
       FROM topup_transactions t ${where.sql}`,
    )
    .get(...where.values) as TopupSummary;
  return {
    total: Number(row.total) || 0,
    completed: Number(row.completed) || 0,
    pending: Number(row.pending) || 0,
    failed: Number(row.failed) || 0,
    completedSatang: Number(row.completedSatang) || 0,
  };
}

export type TopupTotals = {
  /** Every top-up transaction ever created. */
  total: number;
  /** Completed transactions, regardless of when they completed. */
  completed: number;
  /** Money actually credited by completed transactions. */
  creditedSatang: number;
  /** Requested amounts (falls back to credited for coupons/legacy rows). */
  requestedSatang: number;
};

/**
 * All-time top-up aggregate for the admin overview. This is intentionally not
 * range-bound: the dashboard pairs it with the selected-period stats so the
 * lifetime totals stay visible while a period is filtered.
 */
export function readTopupTotals(database: Database.Database): TopupTotals {
  ensureTopupSchema(database);
  const row = database
    .prepare(
      `SELECT
         count(*) AS total,
         COALESCE(SUM(CASE WHEN status = 'completed' THEN 1 ELSE 0 END), 0) AS completed,
         COALESCE(SUM(CASE WHEN status = 'completed' THEN credited_satang ELSE 0 END), 0) AS creditedSatang,
         COALESCE(SUM(COALESCE(requested_satang, credited_satang, 0)), 0) AS requestedSatang
       FROM topup_transactions`,
    )
    .get() as TopupTotals;
  return {
    total: Number(row.total) || 0,
    completed: Number(row.completed) || 0,
    creditedSatang: Number(row.creditedSatang) || 0,
    requestedSatang: Number(row.requestedSatang) || 0,
  };
}

export function listCustomerTopups(
  database: Database.Database,
  userId: string,
  raw: RawFilters,
): Omit<TopupHistoryResponse, "balanceSatang"> {
  ensureTopupSchema(database);
  const { filters, range, page, pageSize } = parseTopupFilters(raw, {
    allowAll: true,
    defaultPeriod: "all",
  });
  const where = buildWhere(filters, range, { userId });
  const total = (
    database
      .prepare(
        `SELECT count(*) AS count FROM topup_transactions t ${where.sql}`,
      )
      .get(...where.values) as { count: number }
  ).count;
  const pagination = paginationFor(page, pageSize, total);
  const rows = database
    .prepare(
      `SELECT t.* FROM topup_transactions t ${where.sql}
       ORDER BY ${orderBy(filters.sort)} LIMIT ? OFFSET ?`,
    )
    .all(...where.values, pagination.pageSize, (pagination.page - 1) * pagination.pageSize) as TopupRow[];
  return {
    summary: summaryFor(database, where),
    entries: rows.map(topupEntryFromRow),
    pagination,
    filters,
    rangeError: range.error,
  };
}

export function getCustomerTopupDetail(
  database: Database.Database,
  userId: string,
  topupId: string,
) {
  ensureTopupSchema(database);
  const row = requireTopupRow(database, topupId);
  if (row.user_id !== userId) {
    throw new TopupError("transaction-not-found", "ไม่พบรายการเติมเงิน", 404);
  }
  return getTopupDetail(database, row);
}

export function getAdminTopupDetail(
  database: Database.Database,
  topupId: string,
): AdminTopupDetail {
  ensureTopupSchema(database);
  const row = requireTopupRow(database, topupId);
  const detail = topupDetailFromRow(row, getTopupDetail(database, row).events);
  const attempts = getTopupAttempts(database, row.id).map((attempt) => ({
    id: attempt.id,
    attemptNo: attempt.attempt_no,
    provider: attempt.provider,
    outcome: attempt.outcome,
    httpStatus: attempt.http_status,
    errorCode: attempt.error_code,
    errorMessage: attempt.error_message,
    transRef: attempt.trans_ref,
    amountSatang: attempt.amount_satang,
    durationMs: attempt.duration_ms,
    retryAfterSeconds: attempt.retry_after_seconds,
    requestSummary: attempt.request_summary,
    responseExcerpt: attempt.response_excerpt,
    createdAt: attempt.created_at,
  }));
  const canReview = isReviewableTopup(row);
  const review = canReview ? compareReview(row) : null;
  return {
    ...detail,
    userId: row.user_id,
    userName: row.user_name,
    userEmail: row.user_email,
    providerRequestId: row.provider_request_id,
    attempts,
    canReview,
    review: review
      ? { outcome: review.outcome, message: review.message }
      : null,
  };
}

function statsForRange(
  database: Database.Database,
  start: number,
  end: number,
): AdminTopupStats {
  const created = database
    .prepare(
      `SELECT
         COALESCE(SUM(COALESCE(requested_satang, credited_satang, 0)), 0) AS requestedSatang,
         count(*) AS total,
         COALESCE(SUM(CASE WHEN status IN ('pending','processing') THEN 1 ELSE 0 END), 0) AS pending,
         COALESCE(SUM(CASE WHEN status IN ('failed','expired') THEN 1 ELSE 0 END), 0) AS failed
       FROM topup_transactions WHERE created_at >= ? AND created_at < ?`,
    )
    .get(start, end) as {
    requestedSatang: number;
    total: number;
    pending: number;
    failed: number;
  };
  const completed = database
    .prepare(
      `SELECT
         COALESCE(SUM(CASE WHEN status = 'completed' THEN 1 ELSE 0 END), 0) AS completed,
         COALESCE(SUM(CASE WHEN status = 'completed' THEN credited_satang ELSE 0 END), 0) AS creditedSatang
       FROM topup_transactions
       WHERE status = 'completed' AND completed_at >= ? AND completed_at < ?`,
    )
    .get(start, end) as { completed: number; creditedSatang: number };
  return {
    requestedSatang: Number(created.requestedSatang) || 0,
    total: Number(created.total) || 0,
    completed: Number(completed.completed) || 0,
    pending: Number(created.pending) || 0,
    failed: Number(created.failed) || 0,
    creditedSatang: Number(completed.creditedSatang) || 0,
  };
}

function strftimePattern(bucket: "hour" | "day" | "month") {
  if (bucket === "hour") return "%H";
  if (bucket === "month") return "%Y-%m";
  return "%Y-%m-%d";
}

type ChannelCountRow = { bucket: string; channel: string; count: number };
type ChannelAmountRow = { bucket: string; channel: string; amount: number };

function fillChart(
  database: Database.Database,
  range: ResolvedDateRange,
  channelFilter: TopupFilters["channel"],
): TopupChartPoint[] {
  if (range.start === null || range.end === null) return [];
  const pattern = strftimePattern(range.bucket);
  const channelClause =
    channelFilter === "all" ? "" : "AND channel = ?";
  const channelValue = channelFilter === "all" ? [] : [channelFilter];
  const counts = database
    .prepare(
      `SELECT strftime(?, created_at / 1000, 'unixepoch', '+7 hours') AS bucket,
              channel, count(*) AS count
       FROM topup_transactions
       WHERE created_at >= ? AND created_at < ? ${channelClause}
       GROUP BY bucket, channel`,
    )
    .all(pattern, range.start, range.end, ...channelValue) as ChannelCountRow[];
  const amounts = database
    .prepare(
      `SELECT strftime(?, completed_at / 1000, 'unixepoch', '+7 hours') AS bucket,
              channel, COALESCE(SUM(credited_satang), 0) AS amount
       FROM topup_transactions
       WHERE status = 'completed' AND completed_at >= ? AND completed_at < ? ${channelClause}
       GROUP BY bucket, channel`,
    )
    .all(pattern, range.start, range.end, ...channelValue) as ChannelAmountRow[];

  const countMap = new Map(
    counts.map((row) => [`${row.bucket}:${row.channel}`, Number(row.count)]),
  );
  const amountMap = new Map(
    amounts.map((row) => [`${row.bucket}:${row.channel}`, Number(row.amount)]),
  );
  return enumerateRangeBuckets(range.start, range.end, range.bucket).map((bucket) => {
    const get = (map: Map<string, number>, channel: string) =>
      map.get(`${bucket.key}:${channel}`) ?? 0;
    return {
      key: bucket.key,
      label: bucket.label,
      truemoneySatang: get(amountMap, "truemoney"),
      promptpaySatang: get(amountMap, "promptpay"),
      bankTransferSatang: get(amountMap, "bank_transfer"),
      couponSatang: get(amountMap, "coupon"),
      truemoneyCount: get(countMap, "truemoney"),
      promptpayCount: get(countMap, "promptpay"),
      bankTransferCount: get(countMap, "bank_transfer"),
      couponCount: get(countMap, "coupon"),
    };
  });
}

export function listAdminTopups(
  database: Database.Database,
  raw: RawFilters,
): AdminTopupDashboardResponse {
  ensureTopupSchema(database);
  const { filters, range, page, pageSize } = parseTopupFilters(raw, {
    allowAll: false,
    defaultPeriod: "30d",
  });
  const where = buildWhere(filters, range, { admin: true });
  const total = (
    database
      .prepare(
        `SELECT count(*) AS count FROM topup_transactions t
         LEFT JOIN user u ON u.id = t.user_id ${where.sql}`,
      )
      .get(...where.values) as { count: number }
  ).count;
  const pagination = paginationFor(page, pageSize, total);
  const rows = database
    .prepare(
      `SELECT t.*, COALESCE(u.name, t.user_name) AS user_display_name,
              COALESCE(u.email, t.user_email) AS user_display_email
       FROM topup_transactions t
       LEFT JOIN user u ON u.id = t.user_id
       ${where.sql}
       ORDER BY ${orderBy(filters.sort)}
       LIMIT ? OFFSET ?`,
    )
    .all(
      ...where.values,
      pagination.pageSize,
      (pagination.page - 1) * pagination.pageSize,
    ) as AdminTopupRow[];
  const entries: AdminTopupEntry[] = rows.map((row) => ({
    ...topupEntryFromRow(row),
    userId: row.user_id,
    userName: row.user_display_name || row.user_name,
    userEmail: row.user_display_email || row.user_email,
    slipProvider: row.slip_provider,
    slipTransRef: row.slip_trans_ref,
    slipAmountSatang: row.slip_amount_satang,
    canReview: isReviewableTopup(row),
  }));

  const start = range.start ?? Date.now() - 29 * 24 * 60 * 60 * 1_000;
  const end = range.end ?? Date.now() + 1;
  const previousStart = range.previousStart ?? start - (end - start);
  const previousEnd = range.previousEnd ?? start;
  const stats = statsForRange(database, start, end);
  const previous = statsForRange(database, previousStart, previousEnd);
  const chart = fillChart(database, range, filters.channel);
  const chartPreviousCreated = database
    .prepare(
      `SELECT COALESCE(SUM(COALESCE(requested_satang, credited_satang, 0)), 0) AS requestedSatang,
              count(*) AS total
       FROM topup_transactions WHERE created_at >= ? AND created_at < ?`,
    )
    .get(previousStart, previousEnd) as { requestedSatang: number; total: number };
  const chartPreviousCompleted = database
    .prepare(
      `SELECT COALESCE(SUM(credited_satang), 0) AS creditedSatang
       FROM topup_transactions
       WHERE status = 'completed' AND completed_at >= ? AND completed_at < ?`,
    )
    .get(previousStart, previousEnd) as { creditedSatang: number };

  return {
    stats,
    previous,
    chart,
    chartPrevious: {
      requestedSatang: Number(chartPreviousCreated.requestedSatang) || 0,
      creditedSatang: Number(chartPreviousCompleted.creditedSatang) || 0,
      total: Number(chartPreviousCreated.total) || 0,
    },
    entries,
    pagination,
    filters,
    rangeError: range.error,
  };
}
