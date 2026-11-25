import { ControllerError } from "@/lib/controller-client";
import { integer, machineApi, segment, text } from "@/lib/machine-api";
import {
  RENTAL_HOUR_SECONDS,
  RentalPaymentError,
  chargeRentalExtension,
  getRentalPayment,
  releaseRentalExtension,
} from "@/lib/rental-payments";
import { authDatabase, type ControllerLeaseRecord } from "@/lib/rental-service";
import { legacyPaymentIdForLease } from "@/lib/legacy-lease-payments";

const noStore = { "Cache-Control": "no-store" };

function extendErrorResponse(error: unknown) {
  if (error instanceof RentalPaymentError)
    return Response.json(
      { error: error.message, code: error.code },
      { status: error.status, headers: noStore },
    );
  if (error instanceof ControllerError && error.status === 503)
    return Response.json(
      { error: "ระบบยังไม่ยืนยันการต่อเวลา กรุณาลองใหม่", code: "controller-unavailable" },
      { status: 503, headers: noStore },
    );
  return null;
}

/** Maps a failed lease lookup to the renter-facing extension errors. */
function leaseLookupError(error: unknown): never {
  if (error instanceof ControllerError) {
    if (error.status === 404)
      throw new RentalPaymentError("payment-not-found", "ไม่พบรายการเช่านี้", 404);
    if (error.status === 403)
      throw new RentalPaymentError("payment-not-found", "ไม่มีสิทธิ์ต่อเวลาการเช่านี้", 403);
    if (error.status === 503)
      throw new RentalPaymentError(
        "controller-unavailable",
        "ระบบยังไม่ยืนยันการต่อเวลา กรุณาลองใหม่",
        503,
      );
  }
  throw error;
}

export async function POST(
  request: Request,
  { params }: { params: Promise<{ rentalId: string }> },
) {
  const { rentalId } = await params;
  return machineApi(
    request,
    false,
    async (actor, call) => {
      const body: unknown = await request.json();
      if (!body || typeof body !== "object" || Array.isArray(body))
        throw new TypeError("Invalid request");
      const record = body as Record<string, unknown>;
      if (
        Object.keys(record).some(
          (key) => !["additionalSeconds", "expectedRevision", "idempotencyKey"].includes(key),
        )
      )
        throw new TypeError("Unexpected request field");
      const additionalSeconds = integer(record.additionalSeconds, RENTAL_HOUR_SECONDS, 86_400);
      if (additionalSeconds % RENTAL_HOUR_SECONDS !== 0)
        throw new TypeError("Invalid extension duration");
      const expectedRevision = integer(record.expectedRevision);
      const idempotencyKey = text(record.idempotencyKey);

      // Resolve the lease (and through it the rental payment) before charging:
      // the Controller lease id is random, so the payment is only reachable via
      // the create idempotency key it exposes as paymentId. A Controller that
      // predates the lease-lookup route returns 404 here; fall back to the URL id
      // so a rolling web/Controller deployment keeps the previous behaviour.
      let lease: ControllerLeaseRecord | null = null;
      try {
        lease = await call<ControllerLeaseRecord>(
          `/internal/leases/${segment(rentalId)}`,
        );
      } catch (error) {
        if (!(error instanceof ControllerError && error.status === 404))
          leaseLookupError(error);
      }
      // The Controller always reports the owner here; only enforce the check when
      // it does. Charging is still gated by the payment owner below and the
      // Controller re-checks ownership on the extend POST.
      if (lease && typeof lease.userId === "string" && lease.userId && lease.userId !== actor.id)
        throw new RentalPaymentError("payment-not-found", "ไม่มีสิทธิ์ต่อเวลาการเช่านี้", 403);

      const database = await authDatabase();
      // Older Controllers do not report paymentId; recover it read-only from the
      // Controller's lease request mapping so extension charging stays correct.
      const paymentId =
        lease?.paymentId ?? legacyPaymentIdForLease(rentalId) ?? rentalId;
      const payment = getRentalPayment(database, paymentId);
      if (!payment || payment.userId !== actor.id)
        throw new RentalPaymentError(
          "payment-not-found",
          "ไม่พบรายการชำระเงินสำหรับการเช่านี้",
          404,
        );

      chargeRentalExtension(database, {
        userId: actor.id,
        paymentId,
        hours: additionalSeconds / RENTAL_HOUR_SECONDS,
        requestKey: idempotencyKey,
      });

      try {
        return await call(`/internal/leases/${segment(rentalId)}/extend`, {
          method: "POST",
          body: JSON.stringify({ additionalSeconds, expectedRevision, idempotencyKey }),
        });
      } catch (error) {
        if (error instanceof ControllerError && error.status === 503)
          // Keep the charge: the Controller may have applied the extension and a
          // retry with the same idempotency key cannot charge twice. Unused
          // extension time is refunded automatically when the lease ends.
          throw new RentalPaymentError(
            "controller-unavailable",
            "ระบบยังไม่ยืนยันการต่อเวลา กรุณาลองใหม่ (จะไม่ถูกหักเงินซ้ำ)",
            503,
          );
        releaseRentalExtension(database, {
          userId: actor.id,
          paymentId,
          requestKey: idempotencyKey,
        });
        if (error instanceof ControllerError) {
          if (error.status === 404)
            throw new RentalPaymentError("payment-not-found", "ไม่พบรายการเช่านี้", 404);
          if (error.status === 403)
            throw new RentalPaymentError("payment-not-found", "ไม่มีสิทธิ์ต่อเวลาการเช่านี้", 403);
          throw new RentalPaymentError(
            "machine-unavailable",
            "ไม่สามารถต่อเวลาได้ในขณะนี้",
            error.status === 409 ? 409 : 400,
          );
        }
        throw error;
      }
    },
    false,
    extendErrorResponse,
  );
}
