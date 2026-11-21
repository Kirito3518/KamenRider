import { machineApi } from "@/lib/machine-api";
import type { Machine } from "@/lib/machine-contracts";
import { formatSpec, ramGibibytes } from "@/lib/machine-specs";

/**
 * Customer-facing machine list. Only explicitly approved machines that the
 * Controller reports as rentable are exposed, and only display-safe fields are
 * returned: no private IPs, MAC address, ports, fingerprints or readiness
 * diagnostics. Hardware specs (CPU/GPU/RAM) are Agent-reported display data.
 *
 * Rentals are billed in whole baht, so a machine whose price is not a positive
 * whole-baht amount is treated as not rentable instead of exposing an invalid
 * price to the customer.
 */
export function GET(request: Request) {
  return machineApi(
    request,
    false,
    async (_, call) =>
      (await call<Machine[]>("/internal/machines"))
        .filter(
          (m) =>
            m.approvalStatus === "approved" &&
            (m.availability ?? "rentable") === "rentable" &&
            (typeof m.operationalState === "string" || m.canRent === true) &&
            Number.isSafeInteger(m.hourlyPriceMinorUnits) &&
            m.hourlyPriceMinorUnits >= 100 &&
            m.hourlyPriceMinorUnits % 100 === 0,
        )
        .map((m) => ({
          id: m.machineId,
          name: m.displayName,
          priceSatang: m.hourlyPriceMinorUnits,
          status: rentalStatus(m.operationalState, m.canRent),
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

function rentalStatus(operationalState: string | undefined, canRent: boolean) {
  switch (operationalState) {
    case "available":
      return canRent ? ("available" as const) : ("notReady" as const);
    case "awaitingPower":
    case "booting":
    case "busy":
    case "clearing":
    case "onlineIdle":
    case "maintenance":
    case "disabled":
      return operationalState;
    default:
      return canRent ? ("available" as const) : ("notReady" as const);
  }
}
