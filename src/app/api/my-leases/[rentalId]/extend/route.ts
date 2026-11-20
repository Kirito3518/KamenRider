import { ControllerError } from "@/lib/controller-client";
import { integer, machineApi, segment, text } from "@/lib/machine-api";
import {
  RENTAL_HOUR_SECONDS,
  RentalPaymentError,
  chargeRentalExtension,
  getRentalPayment,
  releaseRentalExtension,
} from "@/lib/rental-payments";
import { authDatabase } from "@/lib/rental-service";

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

      const database = await authDatabase();
      const payment = getRentalPayment(database, rentalId);
      if (!payment || payment.userId !== actor.id)
        throw new RentalPaymentError(
          "payment-not-found",
          "ไม่พบรายการชำระเงินสำหรับการเช่านี้",
          404,
        );

      chargeRentalExtension(database, {
        userId: actor.id,
        paymentId: rentalId,
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
          paymentId: rentalId,
          requestKey: idempotencyKey,
        });
        if (error instanceof ControllerError) {
          if (error.status === 404)
            throw new RentalPaymentError("payment-not-found", "ไม่พบรายการเช่านี้", 404);
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
