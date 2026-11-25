import type Database from "better-sqlite3";
import { machineApi } from "@/lib/machine-api";
import type { Machine } from "@/lib/machine-contracts";
import {
  listRentalPayments,
  listRentalPaymentsByIds,
  rentalPaymentSummary,
  settleRentalRefunds,
} from "@/lib/rental-payments";
import {
  buildAdminRentalHistoryEntries,
  summarizeRentalHistory,
  type RentalUserInfo,
} from "@/lib/rental-history-data";
import {
  filterRentalEntries,
  paginateRentalEntries,
  parseRentalFilters,
  rentalStatsFromEntries,
  sortRentalEntries,
} from "@/lib/rental-history-query";
import {
  readRentalChart,
  readRentalRevenue,
} from "@/lib/rental-history-admin";
import { authDatabase, type ControllerLeaseRecord } from "@/lib/rental-service";
import { fillLegacyLeasePaymentIds } from "@/lib/legacy-lease-payments";

const ADMIN_LEASE_WINDOW = 500;

function readUsers(database: Database.Database, userIds: string[]) {
  const unique = [...new Set(userIds.filter(Boolean))].slice(0, 500);
  if (unique.length === 0) return [] as RentalUserInfo[];
  const placeholders = unique.map(() => "?").join(",");
  return database
    .prepare(`SELECT id, name, email FROM user WHERE id IN (${placeholders})`)
    .all(...unique) as RentalUserInfo[];
}

export function GET(request: Request) {
  return machineApi(request, true, async (_actor, call) => {
    const database = await authDatabase();
    let controllerAvailable = true;
    let leases: ControllerLeaseRecord[] = [];
    let machines: Machine[] = [];
    try {
      [leases, machines] = await Promise.all([
        call<ControllerLeaseRecord[]>(`/internal/leases?take=${ADMIN_LEASE_WINDOW}`),
        call<Machine[]>("/internal/machines"),
      ]);
    } catch {
      controllerAvailable = false;
    }

    // Compatibility with Controllers that do not expose LeaseDto.paymentId yet.
    if (controllerAvailable) leases = fillLegacyLeasePaymentIds(leases);

    const paymentIdsForLeases = leases.map((lease) => lease.paymentId ?? lease.id);
    const payments = controllerAvailable
      ? listRentalPaymentsByIds(database, paymentIdsForLeases)
      : listRentalPayments(database);

    if (controllerAvailable) settleRentalRefunds(database, leases);

    const settledPayments = controllerAvailable
      ? listRentalPaymentsByIds(database, paymentIdsForLeases)
      : payments;
    const userIds = [
      ...leases.map((lease) => lease.userId),
      ...settledPayments.map((payment) => payment.userId),
    ];
    const entries = buildAdminRentalHistoryEntries({
      leases,
      machines,
      payments: settledPayments,
      users: readUsers(database, userIds),
      includeUnmatchedPayments: !controllerAvailable,
    });
    const windowSummary = summarizeRentalHistory(entries);
    // Revenue totals come from the payment table (all time), not just the lease window.
    const allTime = rentalPaymentSummary(database);

    const raw: Record<string, string | undefined> = {};
    for (const [key, value] of new URL(request.url).searchParams.entries()) {
      raw[key] = value;
    }
    const { filters, range, page, pageSize } = parseRentalFilters(raw, {
      allowAll: false,
      defaultPeriod: "30d",
    });
    const rangeStart = range.start ?? Date.now() - 29 * 24 * 60 * 60 * 1_000;
    const rangeEnd = range.end ?? Date.now() + 1;
    const rangeEntries = filterRentalEntries(
      entries,
      { ...filters, q: "", state: "all", machineId: "all" },
      range,
      { admin: true },
    );
    const previousEntries =
      range.previousStart !== null && range.previousEnd !== null
        ? filterRentalEntries(
            entries,
            { ...filters, q: "", state: "all", machineId: "all" },
            {
              ...range,
              start: range.previousStart,
              end: range.previousEnd,
            },
            { admin: true },
          )
        : [];
    const revenue = readRentalRevenue(database, rangeStart, rangeEnd);
    const previousRevenue =
      range.previousStart !== null && range.previousEnd !== null
        ? readRentalRevenue(database, range.previousStart, range.previousEnd)
        : { revenueSatang: 0, rentalCount: 0 };
    const stats = rentalStatsFromEntries(
      entries,
      rangeEntries,
      revenue.revenueSatang,
      revenue.rentalCount,
    );
    const previous = rentalStatsFromEntries(
      entries,
      previousEntries,
      previousRevenue.revenueSatang,
      previousRevenue.rentalCount,
    );
    const filtered = sortRentalEntries(
      filterRentalEntries(entries, filters, range, { admin: true }),
      filters.sort,
    );
    const paginated = paginateRentalEntries(filtered, page, pageSize);
    const machineOptions = controllerAvailable
      ? machines
          .map((machine) => ({
            machineId: machine.machineId,
            displayName: machine.displayName || machine.machineId,
          }))
          .sort((left, right) => left.displayName.localeCompare(right.displayName))
      : entries
          .map((entry) => ({
            machineId: entry.machineId,
            displayName: entry.machineName,
          }))
          .filter(
            (machine, index, list) =>
              list.findIndex((item) => item.machineId === machine.machineId) === index,
          );

    return {
      controllerAvailable,
      summary: {
        totalRentals: windowSummary.totalRentals,
        activeRentals: windowSummary.activeRentals,
        paidSatang: allTime.revenueSatang + allTime.refundedSatang,
        refundedSatang: allTime.refundedSatang,
        netSatang: allTime.revenueSatang,
      },
      stats,
      previous,
      chart: readRentalChart(database, range),
      chartPrevious: {
        revenueSatang: previousRevenue.revenueSatang,
        count: previousRevenue.rentalCount,
      },
      entries: paginated.entries,
      pagination: paginated.pagination,
      filters,
      rangeError: range.error,
      machines: machineOptions,
    };
  });
}
