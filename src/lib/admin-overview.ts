import "server-only";
import { connection } from "next/server";
import type Database from "better-sqlite3";
import type { AdminMember, MemberTotals } from "@/lib/admin";
import { readMemberTotals } from "@/lib/admin";
import { readAdminMembers, type AdminMembersData } from "@/lib/admin-members-data";
import { getAuth } from "@/lib/auth";
import { resolveDateRange, type ResolvedDateRange } from "@/lib/date-range";
import type { Machine } from "@/lib/machine-contracts";
import { readMachinesSnapshot } from "@/lib/machine-read";
import { summarizeMachines, type MachineSummary } from "@/lib/machine-summary";
import { readRentalChart, readRentalRevenue } from "@/lib/rental-history-admin";
import type { RentalChartPoint } from "@/lib/rental-history";
import { ensureRentalSchema, rentalPaymentSummary } from "@/lib/rental-payments";
import { requireAdminSession } from "@/lib/session";
import {
  listAdminTopups,
  readTopupTotals,
  type TopupTotals,
} from "@/lib/topup/history";
import { countPendingSlipChannelTopups } from "@/lib/topup/settings";
import type {
  AdminTopupEntry,
  AdminTopupStats,
  TopupChartPoint,
} from "@/lib/topup/types";

/**
 * Server-only data for the `/admin/overview` dashboard.
 *
 * Every loader re-checks the admin session (defense in depth on top of the admin
 * layout) and reads the same SQLite handle the rest of the admin screens use.
 * The dashboard is rendered from several independently-streamed sections, so
 * the money loaders never call the Controller and a Controller outage cannot
 * delay the member/top-up/rental numbers.
 *
 * The page also never receives raw Controller `Machine` rows: only summarized
 * counts are forwarded to client components.
 */

export type OverviewSearchParams = Record<string, string | string[] | undefined>;
export type OverviewFilters = Record<string, string | undefined>;

export const OVERVIEW_PERIODS = ["today", "7d", "30d", "90d", "custom"] as const;
export const OVERVIEW_DEFAULT_PERIOD = "30d";
export const OVERVIEW_LIST_LIMIT = 5;

/** Page/search-param values arrive as `string | string[]`; keep the first value. */
export function flattenOverviewSearchParams(raw: OverviewSearchParams): OverviewFilters {
  return Object.fromEntries(
    Object.entries(raw).map(([key, value]) => [key, Array.isArray(value) ? value[0] : value]),
  );
}

/** UI periods reuse `resolveDateRange`'s allow-list; "all" is not offered here. */
export function resolveOverviewRange(
  filters: OverviewFilters,
  now = Date.now(),
): ResolvedDateRange {
  return resolveDateRange(
    { period: filters.period, from: filters.from, to: filters.to },
    now,
    { allowAll: false, defaultPeriod: OVERVIEW_DEFAULT_PERIOD },
  );
}

export type OverviewRange = {
  range: ResolvedDateRange;
  rangeError: string;
  loadedAt: number;
};

/** Range-only loader for the dashboard status line and the period picker. */
export async function getAdminOverviewStatus(
  raw: OverviewSearchParams,
): Promise<OverviewRange> {
  await requireAdminSession();
  // `Date.now()` below is request data: without an explicit dynamic access the
  // Cache Components prerender logs "unstable value Date.now()" for the shell.
  await connection();
  const filters = flattenOverviewSearchParams(raw);
  const now = Date.now();
  const range = resolveOverviewRange(filters, now);
  return { range, rangeError: range.error, loadedAt: now };
}

function boundedRange(range: ResolvedDateRange) {
  return { start: range.start ?? 0, end: range.end ?? 0 };
}

type OverviewContext = {
  database: Database.Database;
  range: ResolvedDateRange;
  now: number;
};

async function openOverview(raw: OverviewSearchParams): Promise<OverviewContext> {
  await requireAdminSession();
  const database = (await getAuth()).options.database as unknown as Database.Database;
  const now = Date.now();
  const range = resolveOverviewRange(flattenOverviewSearchParams(raw), now);
  return { database, range, now };
}

// ---------------------------------------------------------------------------
// Totals: all-time member KPIs plus new sign-ups for the selected period.
// ---------------------------------------------------------------------------

export type OverviewSignupCounts = { newMembers: number; previousMembers: number };

/**
 * New sign-ups in a window and in the immediately preceding window, using the
 * same Bangkok-time boundaries and ISO/epoch timestamp handling as the members
 * page (`readAdminMembers`). Kept local so the KPI section does not have to load
 * a member list it will not render.
 */
export function readSignupCounts(
  database: Database.Database,
  start: number,
  end: number,
): OverviewSignupCounts {
  const created = `(CASE WHEN typeof(createdAt) IN ('integer','real') THEN createdAt ELSE CAST(round(unixepoch(createdAt, 'subsec') * 1000) AS INTEGER) END)`;
  const row = database
    .prepare(
      `SELECT
         COALESCE(SUM(CASE WHEN ${created} >= ? AND ${created} < ? THEN 1 ELSE 0 END), 0) AS newMembers,
         COALESCE(SUM(CASE WHEN ${created} >= ? AND ${created} < ? THEN 1 ELSE 0 END), 0) AS previousMembers
       FROM user`,
    )
    .get(start, end, start - (end - start), start) as {
    newMembers: number;
    previousMembers: number;
  };
  return {
    newMembers: Number(row.newMembers) || 0,
    previousMembers: Number(row.previousMembers) || 0,
  };
}

export type OverviewTotals = OverviewRange & {
  memberTotals: MemberTotals;
  signups: OverviewSignupCounts;
  topupTotals: TopupTotals;
};

export async function getAdminOverviewTotals(
  raw: OverviewSearchParams,
): Promise<OverviewTotals> {
  const { database, range, now } = await openOverview(raw);
  const { start, end } = boundedRange(range);
  return {
    range,
    rangeError: range.error,
    loadedAt: now,
    memberTotals: readMemberTotals(database),
    signups: readSignupCounts(database, start, end),
    topupTotals: readTopupTotals(database),
  };
}

// ---------------------------------------------------------------------------
// Members: sign-up chart plus the newest accounts.
// ---------------------------------------------------------------------------

export type OverviewMembers = OverviewRange & {
  stats: AdminMembersData["stats"];
  chart: AdminMembersData["chart"];
  latest: AdminMember[];
};

function memberList(data: AdminMembersData): AdminMember[] {
  return data.members
    .slice(0, OVERVIEW_LIST_LIMIT)
    .map(({ id, name, email, image, role, balanceSatang }) => ({
      id,
      name,
      email,
      image,
      role,
      balanceSatang,
    }));
}

export async function getAdminOverviewMembers(
  raw: OverviewSearchParams,
): Promise<OverviewMembers> {
  const { database, range, now } = await openOverview(raw);
  // The members page itself filters with `{period:"custom", from, to}`; reusing
  // exactly that shape keeps the chart/labels identical to `/admin/users`.
  const data = readAdminMembers(
    database,
    { period: "custom", from: range.from, to: range.to },
    now,
  );
  return {
    range,
    rangeError: range.error,
    loadedAt: now,
    stats: data.stats,
    chart: data.chart,
    latest: memberList(data),
  };
}

// ---------------------------------------------------------------------------
// Top-ups: range stats/charts, newest transactions and the lifetime aggregate.
// ---------------------------------------------------------------------------

export type OverviewTopups = OverviewRange & {
  stats: AdminTopupStats;
  previous: AdminTopupStats;
  chart: TopupChartPoint[];
  chartPrevious: { requestedSatang: number; creditedSatang: number; total: number };
  latest: AdminTopupEntry[];
  pendingSlipReviews: number;
  totals: TopupTotals;
};

export async function getAdminOverviewTopups(
  raw: OverviewSearchParams,
): Promise<OverviewTopups> {
  const { database, range, now } = await openOverview(raw);
  // Same `{period, from, to}` shape as `/admin/topups` (page size pinned to the
  // overview list length) so the numbers agree with the history screen.
  const data = listAdminTopups(database, {
    period: "custom",
    from: range.from,
    to: range.to,
    pageSize: String(OVERVIEW_LIST_LIMIT),
  });
  return {
    range,
    rangeError: range.error,
    loadedAt: now,
    stats: data.stats,
    previous: data.previous,
    chart: data.chart,
    chartPrevious: data.chartPrevious,
    latest: data.entries.slice(0, OVERVIEW_LIST_LIMIT),
    pendingSlipReviews: countPendingSlipChannelTopups(database),
    totals: readTopupTotals(database),
  };
}

// ---------------------------------------------------------------------------
// Rentals: money taken from the payment table (never settled from here).
// ---------------------------------------------------------------------------

export type OverviewRentalRangeTotals = {
  /** Gross paid: initial charge + confirmed extensions. */
  paidSatang: number;
  /** Prorated refunds in the window. */
  refundedSatang: number;
  /** paidSatang - refundedSatang (shop revenue). */
  netSatang: number;
  rentalCount: number;
};

/**
 * Read-only aggregate for confirmed rental payments created in a window. The
 * overview must never call `settleRentalRefunds`; it only reports money that the
 * rental system already wrote.
 */
export function readRentalRangeTotals(
  database: Database.Database,
  start: number,
  end: number,
): OverviewRentalRangeTotals {
  ensureRentalSchema(database);
  const row = database
    .prepare(
      `SELECT
         COALESCE(SUM(amount_satang + extension_satang), 0) AS paidSatang,
         COALESCE(SUM(refunded_satang), 0) AS refundedSatang,
         COALESCE(SUM(amount_satang + extension_satang - refunded_satang), 0) AS netSatang,
         count(*) AS rentalCount
       FROM rental_payments
       WHERE status = 'confirmed' AND created_at >= ? AND created_at < ?`,
    )
    .get(start, end) as OverviewRentalRangeTotals;
  return {
    paidSatang: Number(row.paidSatang) || 0,
    refundedSatang: Number(row.refundedSatang) || 0,
    netSatang: Number(row.netSatang) || 0,
    rentalCount: Number(row.rentalCount) || 0,
  };
}

export type OverviewRentalWindow = OverviewRentalRangeTotals & {
  /** Net revenue reported by `readRentalRevenue` (the admin rentals source). */
  revenueSatang: number;
};

export type OverviewRentalEntry = {
  id: string;
  userId: string;
  userName: string;
  userEmail: string;
  machineId: string;
  machineName: string;
  status: string;
  paidSatang: number;
  refundedSatang: number;
  netSatang: number;
  createdAt: number;
};

/** Newest rental payments, bounded so the overview never scans the full table. */
export function readRecentRentalPayments(
  database: Database.Database,
  limit = OVERVIEW_LIST_LIMIT,
): OverviewRentalEntry[] {
  ensureRentalSchema(database);
  const rows = database
    .prepare(
      `SELECT p.id, p.user_id, p.machine_id, p.machine_name, p.status,
              p.amount_satang, p.extension_satang, p.refunded_satang, p.created_at,
              COALESCE(u.name, p.user_id) AS user_name,
              COALESCE(u.email, '') AS user_email
       FROM rental_payments p
       LEFT JOIN user u ON u.id = p.user_id
       ORDER BY p.created_at DESC, p.rowid DESC
       LIMIT ?`,
    )
    .all(limit) as {
    id: string;
    user_id: string;
    machine_id: string;
    machine_name: string;
    status: string;
    amount_satang: number;
    extension_satang: number;
    refunded_satang: number;
    created_at: number;
    user_name: string;
    user_email: string;
  }[];
  return rows.map((row) => {
    const paidSatang = (Number(row.amount_satang) || 0) + (Number(row.extension_satang) || 0);
    return {
      id: row.id,
      userId: row.user_id,
      userName: row.user_name,
      userEmail: row.user_email,
      machineId: row.machine_id,
      machineName: row.machine_name,
      status: row.status,
      paidSatang,
      refundedSatang: Number(row.refunded_satang) || 0,
      netSatang: paidSatang - (Number(row.refunded_satang) || 0),
      createdAt: row.created_at,
    };
  });
}

export type OverviewRentals = OverviewRange & {
  current: OverviewRentalWindow;
  previous: OverviewRentalWindow;
  chart: RentalChartPoint[];
  summary: { totalRentals: number; revenueSatang: number; refundedSatang: number };
  latest: OverviewRentalEntry[];
};

function rentalWindow(database: Database.Database, range: ResolvedDateRange): OverviewRentalWindow {
  const { start, end } = boundedRange(range);
  const totals = readRentalRangeTotals(database, start, end);
  const revenue = readRentalRevenue(database, start, end);
  return {
    ...totals,
    netSatang: revenue.revenueSatang,
    rentalCount: revenue.rentalCount,
    revenueSatang: revenue.revenueSatang,
  };
}

export async function getAdminOverviewRentals(
  raw: OverviewSearchParams,
): Promise<OverviewRentals> {
  const { database, range, now } = await openOverview(raw);
  const previousRange: ResolvedDateRange = {
    ...range,
    start: range.previousStart,
    end: range.previousEnd,
  };
  return {
    range,
    rangeError: range.error,
    loadedAt: now,
    current: rentalWindow(database, range),
    previous:
      range.previousStart !== null && range.previousEnd !== null
        ? rentalWindow(database, previousRange)
        : { paidSatang: 0, refundedSatang: 0, netSatang: 0, rentalCount: 0, revenueSatang: 0 },
    chart: readRentalChart(database, range),
    summary: rentalPaymentSummary(database),
    latest: readRecentRentalPayments(database, OVERVIEW_LIST_LIMIT),
  };
}

// ---------------------------------------------------------------------------
// Machines: Controller-backed, summarized so only counts reach client code.
// ---------------------------------------------------------------------------

export type OverviewMachines = {
  available: boolean;
  summary: MachineSummary;
  now: number;
  loadedAt: number;
};

async function readSummarizedMachines(): Promise<OverviewMachines> {
  const session = await requireAdminSession();
  await connection();
  const now = Date.now();
  let snapshot: { available: boolean; machines: Machine[] } = { available: false, machines: [] };
  try {
    snapshot = await readMachinesSnapshot({ id: session.user.id, role: session.user.role });
  } catch {
    // The Controller is an optional dependency for the overview; any failure
    // degrades the machine section instead of failing the page.
    snapshot = { available: false, machines: [] };
  }
  return {
    available: snapshot.available,
    summary: summarizeMachines(snapshot.machines, now),
    now,
    loadedAt: now,
  };
}

export async function getAdminOverviewMachines(): Promise<OverviewMachines> {
  return readSummarizedMachines();
}

// ---------------------------------------------------------------------------
// Single-snapshot aggregate (machine read first, then every DB query).
// ---------------------------------------------------------------------------

export type AdminOverview = {
  range: ResolvedDateRange;
  rangeError: string;
  loadedAt: number;
  totals: {
    member: MemberTotals;
    signups: OverviewSignupCounts;
    topups: TopupTotals;
    pendingSlipReviews: number;
  };
  members: { stats: AdminMembersData["stats"]; chart: AdminMembersData["chart"]; latest: AdminMember[] };
  topups: {
    stats: AdminTopupStats;
    previous: AdminTopupStats;
    chart: TopupChartPoint[];
    chartPrevious: { requestedSatang: number; creditedSatang: number; total: number };
    latest: AdminTopupEntry[];
  };
  rentals: {
    current: OverviewRentalWindow;
    previous: OverviewRentalWindow;
    chart: RentalChartPoint[];
    summary: { totalRentals: number; revenueSatang: number; refundedSatang: number };
    latest: OverviewRentalEntry[];
  };
  machines: {
    available: boolean;
    /** Raw Controller rows stay server-side; never forwarded to client components. */
    snapshot: Machine[];
    summary: MachineSummary;
    now: number;
  };
};

/**
 * Whole-dashboard snapshot. The Controller snapshot is read first, then all
 * database aggregates run on one auth-database handle. The page itself uses the
 * per-section loaders above so the Controller cannot block the money sections.
 */
export async function getAdminOverview(raw: OverviewSearchParams): Promise<AdminOverview> {
  const session = await requireAdminSession();
  const snapshot = await readMachinesSnapshot({
    id: session.user.id,
    role: session.user.role,
  }).catch(() => ({ available: false, machines: [] as Machine[], readAt: Date.now() }));
  const database = (await getAuth()).options.database as unknown as Database.Database;
  const now = Date.now();
  const range = resolveOverviewRange(flattenOverviewSearchParams(raw), now);
  const members = readAdminMembers(
    database,
    { period: "custom", from: range.from, to: range.to },
    now,
  );
  const topups = listAdminTopups(database, {
    period: "custom",
    from: range.from,
    to: range.to,
    pageSize: String(OVERVIEW_LIST_LIMIT),
  });
  const previousRange: ResolvedDateRange = {
    ...range,
    start: range.previousStart,
    end: range.previousEnd,
  };
  const { start, end } = boundedRange(range);
  return {
    range,
    rangeError: range.error,
    loadedAt: now,
    totals: {
      member: readMemberTotals(database),
      signups: readSignupCounts(database, start, end),
      topups: readTopupTotals(database),
      pendingSlipReviews: countPendingSlipChannelTopups(database),
    },
    members: {
      stats: members.stats,
      chart: members.chart,
      latest: memberList(members),
    },
    topups: {
      stats: topups.stats,
      previous: topups.previous,
      chart: topups.chart,
      chartPrevious: topups.chartPrevious,
      latest: topups.entries.slice(0, OVERVIEW_LIST_LIMIT),
    },
    rentals: {
      current: rentalWindow(database, range),
      previous:
        range.previousStart !== null && range.previousEnd !== null
          ? rentalWindow(database, previousRange)
          : { paidSatang: 0, refundedSatang: 0, netSatang: 0, rentalCount: 0, revenueSatang: 0 },
      chart: readRentalChart(database, range),
      summary: rentalPaymentSummary(database),
      latest: readRecentRentalPayments(database, OVERVIEW_LIST_LIMIT),
    },
    machines: {
      available: snapshot.available,
      snapshot: snapshot.machines,
      summary: summarizeMachines(snapshot.machines, now),
      now,
    },
  };
}
