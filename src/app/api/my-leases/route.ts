import { machineApi, segment } from "@/lib/machine-api";
import type { Lease } from "@/lib/machine-contracts";
export function GET(r: Request) {
  return machineApi(r, false, async (actor, call) =>
    Promise.all(
      (await call<Lease[]>(`/internal/users/${segment(actor.id)}/leases`)).map(
        async (lease) => ({
          ...lease,
          connection: await call(
            `/internal/leases/${segment(lease.id)}/connection`,
          ),
        }),
      ),
    ),
  );
}
