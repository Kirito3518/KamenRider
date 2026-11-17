import { machineApi } from "@/lib/machine-api";
import type { Machine } from "@/lib/machine-contracts";
import { formatSpec, ramGibibytes } from "@/lib/machine-specs";

/**
 * Customer-facing machine list. Only explicitly approved machines that the
 * Controller reports as rentable are exposed, and only display-safe fields are
 * returned: no private IPs, MAC address, ports, fingerprints or readiness
 * diagnostics. Hardware specs (CPU/GPU/RAM) are Agent-reported display data.
 */
export function GET(request: Request) {
  return machineApi(
    request,
    false,
    async (_, call) =>
      (await call<Machine[]>("/internal/machines"))
        .filter((m) => m.approvalStatus === "approved" && m.canRent === true)
        .map((m) => ({
          id: m.machineId,
          name: m.displayName,
          price: m.hourlyPriceMinorUnits / 100,
          status: m.canRent === true ? "available" : m.readiness === "ready" ? "busy" : m.readiness,
          cpu: formatSpec(m.cpu),
          gpu: formatSpec(m.gpu),
          ram: ramGibibytes(m.ramBytes),
          ramBytes:
            typeof m.ramBytes === "number" &&
            Number.isFinite(m.ramBytes) &&
            m.ramBytes > 0
              ? m.ramBytes
              : 0,
          storage: "—",
          game: "",
        })),
    true,
  );
}
