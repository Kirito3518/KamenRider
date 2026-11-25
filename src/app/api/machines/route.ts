import { machineApi } from "@/lib/machine-api";
import type { Machine } from "@/lib/machine-contracts";
import { toPublicMachines } from "@/lib/public-machines";

/**
 * Customer-facing machine list. Only explicitly approved machines that the
 * Controller reports as rentable are exposed, and only display-safe fields are
 * returned: no private IPs, MAC address, ports, fingerprints or readiness
 * diagnostics. Hardware specs (CPU/GPU/RAM) are Agent-reported display data.
 *
 * The filter/map lives in `@/lib/public-machines` so the live snapshot hub can
 * expose exactly the same public machine shape. Rentals are billed in whole
 * baht, so a machine whose price is not a positive whole-baht amount is treated
 * as not rentable instead of exposing an invalid price to the customer.
 */
export function GET(request: Request) {
  return machineApi(
    request,
    false,
    async (_, call) =>
      toPublicMachines(await call<Machine[]>("/internal/machines")),
    true,
  );
}
