import type {
  AdminRentalHistoryEntry,
  RentalHistoryEntry,
  RentalHistoryState,
  RentalHistorySummary,
} from "@/lib/rental-history";
import { rentalNetSatang } from "@/lib/rental-history";
import type { RentalPayment } from "@/lib/rental-payments";

/**
 * Server-side merge of Controller lease facts (state/time) with the payment
 * records kept in the auth database (money). The client never sees raw
 * Controller or payment rows.
 */

export type ControllerLeaseView = {
  id: string;
  userId: string;
  machineId: string;
  state: string;
  startsAtUtc: string | null;
  expiresAtUtc: string | null;
  endedAtUtc?: string | null;
};

export type ControllerMachineView = {
  machineId: string;
  displayName: string;
};

export type RentalUserInfo = {
  id: string;
  name: string | null;
  email: string | null;
};

const controllerStates = new Set<RentalHistoryState>([
  "preparing",
  "active",
  "ending",
  "completed",
  "failed",
]);

function normalizeLeaseState(state: string): RentalHistoryState {
  return controllerStates.has(state as RentalHistoryState)
    ? (state as RentalHistoryState)
    : "unknown";
}

function paymentState(payment: RentalPayment): RentalHistoryState {
  if (payment.status === "rejected") return "rejected";
  if (payment.status === "pending") return "pending";
  return "unknown";
}

function machineNameFor(
  machineId: string,
  payment: RentalPayment | null,
  machines: Map<string, string>,
) {
  return payment?.machineName || machines.get(machineId) || machineId;
}

function secondsBetween(start: string | null, end: string | null) {
  if (!start || !end) return 0;
  const startMs = Date.parse(start);
  const endMs = Date.parse(end);
  if (!Number.isFinite(startMs) || !Number.isFinite(endMs) || endMs <= startMs) return 0;
  return Math.round((endMs - startMs) / 1000);
}

function baseEntry(
  lease: ControllerLeaseView | null,
  payment: RentalPayment | null,
  machines: Map<string, string>,
): RentalHistoryEntry {
  const machineId = lease?.machineId ?? payment?.machineId ?? "";
  const bookedSeconds =
    payment?.durationSeconds ?? secondsBetween(lease?.startsAtUtc ?? null, lease?.expiresAtUtc ?? null);
  const extensionSeconds = payment?.extensionSeconds ?? 0;
  const paidSatang = payment ? payment.amountSatang + payment.extensionSatang : null;
  const refundedSatang = payment?.refundedSatang ?? 0;
  const createdAtUtc = payment
    ? new Date(payment.createdAt).toISOString()
    : (lease?.startsAtUtc ?? lease?.expiresAtUtc ?? new Date(0).toISOString());
  const entry: RentalHistoryEntry = {
    rentalId: lease?.id ?? payment?.id ?? "",
    machineId,
    machineName: machineNameFor(machineId, payment, machines),
    state: lease ? normalizeLeaseState(lease.state) : payment ? paymentState(payment) : "unknown",
    startsAtUtc: lease?.startsAtUtc ?? null,
    expiresAtUtc: lease?.expiresAtUtc ?? null,
    endedAtUtc: lease?.endedAtUtc ?? null,
    createdAtUtc,
    bookedSeconds,
    extensionSeconds,
    hourlyPriceSatang: payment?.hourlyPriceSatang ?? null,
    paidSatang,
    refundedSatang,
    netSatang: paidSatang === null ? null : rentalNetSatang({ paidSatang, refundedSatang }),
  };
  return entry;
}

function sortNewestFirst(entries: RentalHistoryEntry[]) {
  return entries.sort((left, right) => {
    const delta = Date.parse(right.createdAtUtc) - Date.parse(left.createdAtUtc);
    return delta !== 0 ? delta : right.rentalId.localeCompare(left.rentalId);
  });
}

/**
 * Customer history: every lease the Controller knows about plus payments that
 * never produced a lease (still confirming, or refused and refunded).
 */
export function buildRentalHistoryEntries(input: {
  leases: ControllerLeaseView[];
  machines: ControllerMachineView[];
  payments: RentalPayment[];
  includeLocalOnly?: boolean;
}) {
  const machines = new Map(input.machines.map((machine) => [machine.machineId, machine.displayName]));
  const payments = new Map(input.payments.map((payment) => [payment.id, payment]));
  const entries: RentalHistoryEntry[] = [];
  for (const lease of input.leases) {
    const payment = payments.get(lease.id) ?? null;
    payments.delete(lease.id);
    entries.push(baseEntry(lease, payment, machines));
  }
  if (input.includeLocalOnly !== false) {
    for (const payment of payments.values()) {
      entries.push(baseEntry(null, payment, machines));
    }
  }
  return sortNewestFirst(entries);
}

export function buildAdminRentalHistoryEntries(input: {
  leases: ControllerLeaseView[];
  machines: ControllerMachineView[];
  payments: RentalPayment[];
  users: RentalUserInfo[];
  /**
   * When the Controller cannot be reached the admin history falls back to the
   * payment records alone so money history remains visible.
   */
  includeUnmatchedPayments?: boolean;
}) {
  const users = new Map(input.users.map((user) => [user.id, user]));
  const machines = new Map(input.machines.map((machine) => [machine.machineId, machine.displayName]));
  const payments = new Map(input.payments.map((payment) => [payment.id, payment]));
  const entries: AdminRentalHistoryEntry[] = [];
  const withUser = (entry: RentalHistoryEntry, userId: string): AdminRentalHistoryEntry => {
    const user = users.get(userId);
    return {
      ...entry,
      userId,
      userName: user?.name?.trim() || userId,
      userEmail: user?.email ?? "",
    };
  };
  for (const lease of input.leases) {
    const payment = payments.get(lease.id) ?? null;
    payments.delete(lease.id);
    entries.push(withUser(baseEntry(lease, payment, machines), lease.userId || payment?.userId || ""));
  }
  if (input.includeUnmatchedPayments) {
    for (const payment of payments.values()) {
      entries.push(withUser(baseEntry(null, payment, machines), payment.userId));
    }
  }
  return sortNewestFirst(entries) as AdminRentalHistoryEntry[];
}

const activeLeaseStates = new Set<RentalHistoryState>(["preparing", "active", "ending"]);

export function summarizeRentalHistory(entries: RentalHistoryEntry[]): RentalHistorySummary {
  let paidSatang = 0;
  let refundedSatang = 0;
  let activeRentals = 0;
  for (const entry of entries) {
    if (activeLeaseStates.has(entry.state)) activeRentals += 1;
    paidSatang += entry.paidSatang ?? 0;
    refundedSatang += entry.refundedSatang;
  }
  return {
    totalRentals: entries.length,
    activeRentals,
    paidSatang,
    refundedSatang,
    netSatang: paidSatang - refundedSatang,
  };
}
