import { machineApi, segment } from "@/lib/machine-api";
import type { Lease, LeasePaymentInfo, Machine } from "@/lib/machine-contracts";
import {
  getMemberBalance,
  listRentalPayments,
  reconcilePendingRentals,
  settleRentalRefunds,
  type RentalPayment,
} from "@/lib/rental-payments";
import { authDatabase, createControllerLease, type ControllerLeaseRecord } from "@/lib/rental-service";
import { fillLegacyLeasePaymentIds } from "@/lib/legacy-lease-payments";

type LeaseConnection = { address: string | null; stale: boolean };

const unavailableConnection: LeaseConnection = { address: null, stale: false };

/**
 * The Controller returns a verified `publicIp:moonlightPort` address only while
 * the lease is active. Rebuild the known shape defensively so unexpected
 * Controller fields can never reach the renter's browser.
 */
function connectionFromController(value: unknown): LeaseConnection {
  if (!value || typeof value !== "object" || Array.isArray(value))
    return unavailableConnection;
  const record = value as Record<string, unknown>;
  return {
    address:
      typeof record.address === "string" && record.address.trim()
        ? record.address
        : null,
    stale: record.stale === true,
  };
}

function paymentInfo(payment: RentalPayment): LeasePaymentInfo {
  const paidSatang = payment.amountSatang + payment.extensionSatang;
  return {
    hourlyPriceSatang: payment.hourlyPriceSatang,
    durationSeconds: payment.durationSeconds,
    extensionSeconds: payment.extensionSeconds,
    paidSatang,
    refundedSatang: payment.refundedSatang,
    extendable: payment.status === "confirmed" && payment.refundedSatang < paidSatang,
  };
}

export function GET(r: Request) {
  return machineApi(r, false, async (actor, call) => {
    const database = await authDatabase();
    let leases = await call<ControllerLeaseRecord[]>(
      `/internal/users/${segment(actor.id)}/leases`,
    );
    const machines = await call<Machine[]>("/internal/machines");

    const reconciled = await reconcilePendingRentals(database, actor.id, async (payment) => {
      const outcome = await createControllerLease(call, actor.id, payment);
      if (outcome.kind === "created") return { kind: "created" as const };
      if (outcome.kind === "rejected")
        return { kind: "rejected" as const, reason: outcome.reason };
      return { kind: "unknown" as const };
    });
    if (reconciled.length > 0)
      leases = await call<ControllerLeaseRecord[]>(
        `/internal/users/${segment(actor.id)}/leases`,
      );

    // Older Controllers only expose their random lease id; recover the payment
    // mapping read-only so refund/extend keep working until the Controller upgrade.
    leases = fillLegacyLeasePaymentIds(leases);

    settleRentalRefunds(database, leases, { userId: actor.id });

    const payments = new Map(
      listRentalPayments(database, actor.id).map((payment) => [payment.id, payment]),
    );
    const machineById = new Map(
      machines.map((machine) => [
        machine.machineId,
        {
          displayName: machine.displayName,
          parsecEnabled: machine.parsecEnabled === true,
          agent: {
            reachability:
              machine.readiness === "unreachable"
                ? ("unreachable" as const)
                : machine.readiness === "ready" || machine.readiness === "notReady"
                  ? ("reachable" as const)
                  : ("unknown" as const),
            lastSeenAt: machine.lastSeenAt,
          },
        },
      ]),
    );

    const pending: Lease[] = [];
    for (const payment of payments.values()) {
      if (payment.status !== "pending") continue;
      pending.push({
        id: payment.id,
        machineId: payment.machineId,
        machineDisplayName: payment.machineName || machineById.get(payment.machineId)?.displayName || null,
        state: "pending",
        revision: 0,
        pendingRevision: null,
        startsAtUtc: null,
        expiresAtUtc: null,
        endedAtUtc: null,
        agent: { reachability: "unknown", lastSeenAt: null },
        connection: unavailableConnection,
        payment: paymentInfo(payment),
      });
      payments.delete(payment.id);
    }

    const active = await Promise.all(
      leases.map(async (lease) => {
        const machine = machineById.get(lease.machineId);
        // The Controller lease id is not the payment id; the create idempotency
        // key it reports as paymentId is.
        const paymentKey = lease.paymentId ?? lease.id;
        const payment = payments.get(paymentKey) ?? null;
        payments.delete(paymentKey);
        return {
          ...lease,
          // Agent-reported display name (hostname by default); safe to show the
          // renter because it is already published on the public machine list.
          machineDisplayName: machine?.displayName ?? null,
          agent: machine?.agent ?? {
            reachability: "unknown" as const,
            lastSeenAt: null,
          },
          // Only the owner sees this route and only an active lease has a
          // verified connect address; do not attach one to any other state.
          connection:
            lease.state === "active"
              ? connectionFromController(
                  await call<unknown>(
                    `/internal/leases/${segment(lease.id)}/connection`,
                  ),
                )
              : unavailableConnection,
          parsec:
            lease.state === "active" && machine?.parsecEnabled
              ? await call<import("@/lib/parsec-contracts").ParsecLeaseState>(
                  `/internal/leases/${segment(lease.id)}/parsec`,
                )
              : null,
          payment: payment ? paymentInfo(payment) : null,
        } satisfies Lease;
      }),
    );

    return {
      leases: [...pending, ...active],
      // Refunds settled above are already credited, so the wallet must be read
      // after settlement or the renter would briefly see a stale balance.
      balanceSatang: getMemberBalance(database, actor.id),
    };
  });
}
