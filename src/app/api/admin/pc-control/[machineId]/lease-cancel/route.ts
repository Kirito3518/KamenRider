import { integer, machineApi, segment, text } from "@/lib/machine-api";

export async function POST(
  request: Request,
  { params }: { params: Promise<{ machineId: string }> },
) {
  const { machineId } = await params;
  return machineApi(request, true, async (_actor, call) => {
    const body: unknown = await request.json();
    if (!body || typeof body !== "object" || Array.isArray(body)) throw new TypeError("Invalid request");
    const record = body as Record<string, unknown>;
    const keys = Object.keys(record);
    if (keys.some((key) => !["expectedRevision", "idempotencyKey", "requestId"].includes(key)))
      throw new TypeError("Unexpected request field");
    return call(`/internal/machines/${segment(machineId)}/lease-cancel`, {
      method: "POST",
      body: JSON.stringify({
        expectedRevision: integer(record.expectedRevision, 1),
        idempotencyKey: text(record.idempotencyKey),
        requestId: text(record.requestId),
      }),
    });
  });
}
