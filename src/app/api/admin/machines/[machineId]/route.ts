import { machineApi, segment, integer, text } from "@/lib/machine-api";
export async function PATCH(
  r: Request,
  { params }: { params: Promise<{ machineId: string }> },
) {
  const { machineId } = await params;
  return machineApi(r, true, async (_, call) => {
    const b = await r.json();
    const revision = integer(b.expectedRevision);
    const body = {
      displayName: text(b.displayName),
      hourlyPriceMinorUnits: integer(b.hourlyPriceMinorUnits, 1),
      moonlightPort: integer(b.moonlightPort, 1, 65535),
      sunshinePort: integer(b.sunshinePort, 1, 65535),
    };
    return call(
      `/internal/machines/${segment(machineId)}?expectedRevision=${revision}`,
      { method: "PATCH", body: JSON.stringify(body) },
    );
  });
}
