import { ControllerError } from "@/lib/controller-client";
import { integer, machineApi, segment, text } from "@/lib/machine-api";
import { getMemberBalance, settleRentalRefunds } from "@/lib/rental-payments";
import { authDatabase, type ControllerLeaseRecord } from "@/lib/rental-service";

function cancelError(error: unknown) {
  if (!(error instanceof ControllerError)) return null;
  const noStore = { "Cache-Control": "no-store" };
  if (error.status === 404)
    return Response.json({ error: "ไม่พบรายการเช่านี้" }, { status: 404, headers: noStore });
  if (error.status === 403)
    return Response.json({ error: "ไม่มีสิทธิ์ยกเลิกการเช่านี้" }, { status: 403, headers: noStore });
  if (error.status === 409)
    return Response.json({ error: "รายการเช่านี้เปลี่ยนสถานะแล้ว กรุณารีเฟรช" }, { status: 409, headers: noStore });
  if (error.status === 503)
    return Response.json({ error: "ระบบยังไม่ยืนยันการยกเลิก กรุณาลองใหม่" }, { status: 503, headers: noStore });
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
      if (!body || typeof body !== "object" || Array.isArray(body)) throw new TypeError("Invalid request");
      const record = body as Record<string, unknown>;
      if (Object.keys(record).some((key) => key !== "expectedRevision" && key !== "idempotencyKey"))
        throw new TypeError("Unexpected request field");
      const expectedRevision = integer(record.expectedRevision);
      const idempotencyKey = text(record.idempotencyKey);

      // Read the lease first: it owns the payment mapping (the Controller lease
      // id is random, the payment id is the create idempotency key), and a
      // missing/foreign lease fails before the cancel mutation runs. A Controller
      // that predates the lease-lookup route returns 404: fall back to the legacy
      // cancel path so a rolling web/Controller deployment keeps working.
      let before: ControllerLeaseRecord | null = null;
      try {
        before = await call<ControllerLeaseRecord>(
          `/internal/leases/${segment(rentalId)}`,
        );
      } catch (error) {
        if (!(error instanceof ControllerError && error.status === 404)) throw error;
      }
      const lease = await call<ControllerLeaseRecord>(`/internal/leases/${segment(rentalId)}/cancel`, {
        method: "POST",
        body: JSON.stringify({ expectedRevision, idempotencyKey }),
      });
      const database = await authDatabase();
      const paymentId = lease.paymentId ?? before?.paymentId ?? rentalId;
      // Settle with the resolved payment key so the refund lands even when the
      // Controller lease id differs from the rental payment id.
      const settled = settleRentalRefunds(database, [{ ...lease, paymentId }], {
        userId: actor.id,
      });
      const payment = database
        .prepare("SELECT refunded_satang FROM rental_payments WHERE id=? AND user_id=?")
        .get(paymentId, actor.id) as { refunded_satang?: number } | undefined;
      return {
        lease,
        refundedSatang: settled.refundedSatang,
        paymentRefundedSatang: payment?.refunded_satang ?? 0,
        // Read the wallet after settlement so the credited refund is included.
        balanceSatang: getMemberBalance(database, actor.id),
      };
    },
    false,
    cancelError,
  );
}
