import { machineApi, segment, text } from "@/lib/machine-api";

const pairingId = (value: unknown) => {
  const id = text(value);
  if (id !== "foundation-sunshine-single-pending-slot" && !/^[0-9a-f]{32}$/.test(id))
    throw new TypeError("Invalid pairing slot");
  return id;
};

export async function GET(
  request: Request,
  { params }: { params: Promise<{ machineId: string }> },
) {
  const { machineId } = await params;
  return machineApi(request, true, async (_actor, call) =>
    call(`/internal/machines/${segment(machineId)}/pairing`),
  );
}

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
    if (keys.some((key) => !["pairingId", "pin", "idempotencyKey"].includes(key)))
      throw new TypeError("Unexpected request field");
    const pin = text(record.pin);
    if (!/^[0-9]{4}$/.test(pin)) throw new TypeError("Invalid pairing PIN");
    return call(`/internal/machines/${segment(machineId)}/pairing`, {
      method: "POST",
      body: JSON.stringify({
        pairingId: pairingId(record.pairingId),
        pin,
        idempotencyKey: text(record.idempotencyKey),
      }),
    });
  });
}
