import { machineApi, segment, text } from "@/lib/machine-api";

function sealedSecret(value: unknown) {
  if (!value || typeof value !== "object" || Array.isArray(value))
    throw new TypeError("Invalid sealed secret");
  const record = value as Record<string, unknown>;
  const wrappedKey = text(record.wrappedKey);
  const iv = text(record.iv);
  const ciphertext = text(record.ciphertext);
  if (wrappedKey.length > 512 || iv.length > 64 || ciphertext.length > 8192)
    throw new TypeError("Invalid sealed secret");
  return { wrappedKey, iv, ciphertext };
}

export async function GET(
  r: Request,
  { params }: { params: Promise<{ rentalId: string }> },
) {
  const { rentalId } = await params;
  return machineApi(r, false, (_, call) =>
    call(`/internal/leases/${segment(rentalId)}/parsec`),
  );
}

export async function POST(
  r: Request,
  { params }: { params: Promise<{ rentalId: string }> },
) {
  const { rentalId } = await params;
  return machineApi(r, false, async (actor, call) => {
    const body = await r.json();
    const idempotencyKey = text(body.idempotencyKey);
    if (idempotencyKey.length > 128) throw new TypeError("Invalid key");
    return call(`/internal/leases/${segment(rentalId)}/parsec`, {
      method: "POST",
      body: JSON.stringify({
        userId: actor.id,
        idempotencyKey,
        secret: sealedSecret(body.secret),
      }),
    });
  });
}
