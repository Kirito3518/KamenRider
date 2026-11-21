import { machineApi, segment, integer, text } from "@/lib/machine-api";
export async function PATCH(
  r: Request,
  { params }: { params: Promise<{ machineId: string }> },
) {
  const { machineId } = await params;
  return machineApi(r, true, async (_, call) => {
    const b = await r.json();
    const revision = integer(b.expectedRevision);
    if (b.parsecEnabled !== undefined && typeof b.parsecEnabled !== "boolean")
      throw new TypeError("Invalid parsecEnabled");
    if (
      b.availability !== undefined &&
      !["rentable", "maintenance", "disabled"].includes(String(b.availability))
    )
      throw new TypeError("Invalid availability");
    const availability =
      b.availability === undefined ? undefined : String(b.availability);
    let vncLocalIp: string | null;
    if (b.vncLocalIp === undefined) {
      const machines = await call<Array<{ machineId: string; vncLocalIp: string | null }>>("/internal/machines");
      vncLocalIp = machines.find((machine) => machine.machineId === machineId)?.vncLocalIp ?? null;
    } else {
      vncLocalIp = b.vncLocalIp == null ? null : text(b.vncLocalIp);
    }
    const body = {
      displayName: text(b.displayName),
      hourlyPriceMinorUnits: integer(b.hourlyPriceMinorUnits, 1),
      moonlightPort: integer(b.moonlightPort, 1, 65535),
      sunshinePort: integer(b.sunshinePort, 1, 65535),
      vncLocalIp,
      ...(b.parsecEnabled === undefined ? {} : { parsecEnabled: b.parsecEnabled }),
      ...(availability === undefined ? {} : { availability }),
    };
    return call(
      `/internal/machines/${segment(machineId)}?expectedRevision=${revision}`,
      { method: "PATCH", body: JSON.stringify(body) },
    );
  });
}
