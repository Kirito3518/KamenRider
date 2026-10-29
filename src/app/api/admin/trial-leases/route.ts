import { machineApi, text, integer } from "@/lib/machine-api";
export function POST(r: Request) {
  return machineApi(r, true, async (_, call) => {
    const b = await r.json();
    return call("/internal/trial-leases", {
      method: "POST",
      body: JSON.stringify({
        userId: text(b.userId),
        machineId: text(b.machineId),
        durationSeconds: integer(b.durationSeconds, 1, 86400),
        idempotencyKey: text(b.idempotencyKey),
      }),
    });
  });
}
