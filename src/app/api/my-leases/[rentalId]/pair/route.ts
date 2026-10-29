import { machineApi, segment, text } from "@/lib/machine-api";
export async function GET(
  r: Request,
  { params }: { params: Promise<{ rentalId: string }> },
) {
  const { rentalId } = await params;
  return machineApi(r, false, (_, call) =>
    call(`/internal/leases/${segment(rentalId)}/pair/pending`),
  );
}
export async function POST(
  r: Request,
  { params }: { params: Promise<{ rentalId: string }> },
) {
  const { rentalId } = await params;
  return machineApi(r, false, async (actor, call) => {
    const b = await r.json();
    if (typeof b.pin !== "string" || !/^\d{4}$/.test(b.pin))
      throw new TypeError();
    return call(`/internal/leases/${segment(rentalId)}/pair`, {
      method: "POST",
      body: JSON.stringify({
        userId: actor.id,
        pairingId: text(b.pairingId),
        pin: b.pin,
        idempotencyKey: text(b.idempotencyKey),
      }),
    });
  });
}
