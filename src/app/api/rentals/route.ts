import { ControllerError } from "@/lib/controller-client";
import { integer, machineApi, segment, text } from "@/lib/machine-api";
import type { Machine } from "@/lib/machine-contracts";
import {
  MAX_RENTAL_HOURS,
  RentalPaymentError,
  chargeRental,
  confirmRentalPayment,
  getMemberBalance,
  listRentalPayments,
  reconcilePendingRentals,
  rejectAndRefundRentalPayment,
  settleRentalRefunds,
} from "@/lib/rental-payments";
import {
  buildRentalHistoryEntries,
  summarizeRentalHistory,
} from "@/lib/rental-history-data";
import {
  filterRentalEntries,
  paginateRentalEntries,
  parseRentalFilters,
  sortRentalEntries,
} from "@/lib/rental-history-query";
import {
  authDatabase,
  createControllerLease,
  createControllerLeaseWithRetry,
  type ControllerLeaseRecord,
} from "@/lib/rental-service";
import { fillLegacyLeasePaymentIds } from "@/lib/legacy-lease-payments";

const noStore = { "Cache-Control": "no-store" };

function rentalErrorResponse(error: unknown) {
  if (error instanceof RentalPaymentError)
    return Response.json(
      { error: error.message, code: error.code },
      { status: error.status, headers: noStore },
    );
  if (error instanceof ControllerError && error.status === 503)
    return Response.json(
      {
        error: "ระบบเช่ายังไม่ตอบสนอง กรุณาลองใหม่",
        code: "controller-unavailable",
      },
      { status: 503, headers: noStore },
    );
  return null;
}

function bodyRecord(body: unknown) {
  if (!body || typeof body !== "object" || Array.isArray(body))
    throw new TypeError("Invalid request");
  return body as Record<string, unknown>;
}

export function POST(request: Request) {
  return machineApi(
    request,
    false,
    async (actor, call) => {
      const body = bodyRecord(await request.json());
      if (
        Object.keys(body).some(
          (key) => !["machineId", "hours", "requestId"].includes(key),
        )
      )
        throw new TypeError("Unexpected request field");
      const machineId = text(body.machineId);
      const hours = integer(body.hours, 1, MAX_RENTAL_HOURS);
      const requestId = text(body.requestId);

      const machines = await call<Machine[]>("/internal/machines");
      const machine = machines.find((item) => item.machineId === machineId);
      if (!machine || machine.approvalStatus !== "approved" || machine.canRent !== true)
        throw new RentalPaymentError(
          "machine-unavailable",
          "เครื่องนี้ไม่พร้อมให้เช่าในขณะนี้",
          409,
        );

      const database = await authDatabase();
      const charged = chargeRental(database, {
        userId: actor.id,
        machineId,
        machineName: machine.displayName || machineId,
        hourlyPriceSatang: machine.hourlyPriceMinorUnits,
        hours,
        requestId,
      });

      if (charged.repeated && charged.payment.status === "confirmed")
        return {
          rentalId: charged.payment.id,
          state: "confirmed",
          balanceSatang: charged.balanceSatang,
          repeated: true,
        };
      if (charged.repeated && charged.payment.status === "rejected")
        throw new RentalPaymentError(
          "payment-not-ready",
          charged.payment.rejectReason || "รายการนี้ถูกปฏิเสธและคืนเงินแล้ว",
          409,
        );

      const outcome = await createControllerLeaseWithRetry(call, actor.id, charged.payment);
      if (outcome.kind === "created") {
        const payment = confirmRentalPayment(database, charged.payment.id);
        return {
          rentalId: payment.id,
          state: outcome.lease.state,
          balanceSatang: charged.balanceSatang,
          repeated: charged.repeated,
        };
      }
      if (outcome.kind === "rejected") {
        rejectAndRefundRentalPayment(database, charged.payment.id, outcome.reason);
        throw new RentalPaymentError("machine-unavailable", outcome.reason, 409);
      }
      throw new RentalPaymentError(
        "controller-unavailable",
        "ระบบเช่ายังไม่ตอบสนอง ระบบจะยืนยันหรือคืนเงินอัตโนมัติ กรุณาตรวจสอบที่หน้าประวัติการเช่า",
        503,
      );
    },
    false,
    rentalErrorResponse,
  );
}

export function GET(request: Request) {
  return machineApi(request, false, async (actor, call) => {
    const database = await authDatabase();
    let controllerAvailable = true;
    let leases: ControllerLeaseRecord[] = [];
    let machines: Machine[] = [];
    try {
      [leases, machines] = await Promise.all([
        call<ControllerLeaseRecord[]>(`/internal/users/${segment(actor.id)}/leases`),
        call<Machine[]>("/internal/machines"),
      ]);
    } catch {
      controllerAvailable = false;
    }

    if (controllerAvailable) {
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
      leases = fillLegacyLeasePaymentIds(leases);
      settleRentalRefunds(database, leases, { userId: actor.id });
    }

    const payments = listRentalPayments(database, actor.id);
    const entries = buildRentalHistoryEntries({ leases, machines, payments });
    const raw: Record<string, string | undefined> = {};
    for (const [key, value] of new URL(request.url).searchParams.entries()) {
      raw[key] = value;
    }
    const { filters, range, page, pageSize } = parseRentalFilters(raw, {
      allowAll: true,
      defaultPeriod: "all",
    });
    const filtered = sortRentalEntries(
      filterRentalEntries(entries, filters, range),
      filters.sort,
    );
    const paginated = paginateRentalEntries(filtered, page, pageSize);
    const machineMap = new Map<string, string>();
    for (const entry of entries) {
      if (!machineMap.has(entry.machineId)) {
        machineMap.set(entry.machineId, entry.machineName);
      }
    }
    return {
      balanceSatang: getMemberBalance(database, actor.id),
      controllerAvailable,
      summary: summarizeRentalHistory(entries),
      entries: paginated.entries,
      pagination: paginated.pagination,
      filters,
      rangeError: range.error,
      machines: [...machineMap.entries()]
        .map(([machineId, displayName]) => ({ machineId, displayName }))
        .sort((left, right) => left.displayName.localeCompare(right.displayName)),
    };
  });
}
