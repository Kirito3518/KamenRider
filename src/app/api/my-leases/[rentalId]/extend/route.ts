import { integer, machineApi, segment, text } from "@/lib/machine-api";

export async function POST(
  request: Request,
  { params }: { params: Promise<{ rentalId: string }> },
) {
  const { rentalId } = await params;
  return machineApi(request, false, async (_, call) => {
    const body = await request.json();
    return call(`/internal/leases/${segment(rentalId)}/extend`, {
      method: "POST",
      body: JSON.stringify({
        additionalSeconds: integer(body.additionalSeconds, 1, 86400),
        expectedRevision: integer(body.expectedRevision),
        idempotencyKey: text(body.idempotencyKey),
      }),
    });
  });
}
