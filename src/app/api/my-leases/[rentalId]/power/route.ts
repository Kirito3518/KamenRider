import { ControllerError } from "@/lib/controller-client";
import { machineApi, segment, text } from "@/lib/machine-api";

function powerError(error: unknown) {
  if (!(error instanceof ControllerError)) return null;
  const noStore = { "Cache-Control": "no-store" };
  if (error.status === 404)
    return Response.json({ error: "ไม่พบรายการเช่านี้" }, { status: 404, headers: noStore });
  if (error.status === 403)
    return Response.json({ error: "ไม่มีสิทธิ์เปิดเครื่องนี้" }, { status: 403, headers: noStore });
  if (error.status === 409)
    return Response.json({ error: "เครื่องนี้ยังไม่พร้อมให้เปิด" }, { status: 409, headers: noStore });
  if (error.status === 503)
    return Response.json({ error: "ส่งคำสั่งเปิดเครื่องไม่สำเร็จ กรุณาลองใหม่" }, { status: 503, headers: noStore });
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
    async (_actor, call) => {
      const body: unknown = await request.json();
      if (!body || typeof body !== "object" || Array.isArray(body)) throw new TypeError("Invalid request");
      const record = body as Record<string, unknown>;
      if (Object.keys(record).some((key) => key !== "requestId")) throw new TypeError("Unexpected request field");
      const requestId = text(record.requestId);
      return call(`/internal/leases/${segment(rentalId)}/power`, {
        method: "POST",
        body: JSON.stringify({ requestId }),
      });
    },
    false,
    powerError,
  );
}
