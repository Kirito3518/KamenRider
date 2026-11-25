import type { Machine } from "@/lib/machine-contracts";
import { formatSpec, ramGibibytes } from "@/lib/machine-specs";
import type { MachineStatus, RentalMachine } from "@/lib/rental-machines";

/**
 * Customer-facing machine list mapping. Only explicitly approved machines that
 * the Controller reports as rentable are exposed, and only display-safe fields
 * are returned. Hardware specs (CPU/GPU/RAM) are Agent-reported display data.
 *
 * Rentals are billed in whole baht, so a machine whose price is not a positive
 * whole-baht amount is treated as not rentable instead of exposing an invalid
 * price to the customer.
 *
 * This helper is shared by `GET /api/machines` and the live snapshot hub, so
 * both paths always expose exactly the same public fields.
 */
export function toPublicMachines(machines: Machine[]): RentalMachine[] {
  return machines
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
    }));
}

export function rentalStatus(
  operationalState: string | undefined,
  canRent: boolean,
): MachineStatus {
  switch (operationalState) {
    case "available":
      return canRent ? "available" : "notReady";
    case "awaitingPower":
    case "booting":
    case "busy":
    case "clearing":
    case "onlineIdle":
    case "maintenance":
    case "disabled":
      // Pass the Controller lifecycle state through exactly like the original
      // inline mapper did. `disabled` is only produced for an inconsistent
      // rentable machine; admin availability modes are filtered out above.
      return operationalState as MachineStatus;
    default:
      return canRent ? "available" : "notReady";
  }
}
