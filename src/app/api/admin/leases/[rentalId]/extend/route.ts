import { machineApi, segment, text, integer } from "@/lib/machine-api";
export async function POST(
  r: Request,
  { params }: { params: Promise<{ rentalId: string }> },
) {
  const { rentalId } = await params;
  return machineApi(r, true, async (_, call) => {
    const b = await r.json();
    return call(`/internal/leases/${segment(rentalId)}/extend`, {
      method: "POST",
      body: JSON.stringify({
        additionalSeconds: integer(b.additionalSeconds, 1, 86400),
        expectedRevision: integer(b.expectedRevision),
        idempotencyKey: text(b.idempotencyKey),
      }),
    });
  });
}
