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
import { authDatabase, type ControllerLeaseRecord } from "@/lib/rental-service";

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

    const payments = controllerAvailable
      ? listRentalPaymentsByIds(database, leases.map((lease) => lease.id))
      : listRentalPayments(database);

    if (controllerAvailable) settleRentalRefunds(database, leases);

    const settledPayments = controllerAvailable
      ? listRentalPaymentsByIds(database, leases.map((lease) => lease.id))
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
    return {
      controllerAvailable,
      summary: {
        totalRentals: windowSummary.totalRentals,
        activeRentals: windowSummary.activeRentals,
        paidSatang: allTime.revenueSatang + allTime.refundedSatang,
        refundedSatang: allTime.refundedSatang,
        netSatang: allTime.revenueSatang,
      },
      entries,
    };
  });
}
