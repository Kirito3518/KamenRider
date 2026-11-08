import { machineApi, segment } from "@/lib/machine-api";
import type { Lease, Machine } from "@/lib/machine-contracts";
export function GET(r: Request) {
  return machineApi(r, false, async (actor, call) => {
    const [leases, machines] = await Promise.all([
      call<Lease[]>(`/internal/users/${segment(actor.id)}/leases`),
      call<Machine[]>("/internal/machines"),
    ]);
    const agentByMachineId = new Map(
      machines.map((machine) => [
        machine.machineId,
        {
          reachability:
            machine.readiness === "unreachable"
              ? "unreachable"
              : machine.readiness === "ready" ||
                  machine.readiness === "notReady"
                ? "reachable"
                : "unknown",
          lastSeenAt: machine.lastSeenAt,
        } as const,
      ]),
    );

    return Promise.all(
      leases.map(async (lease) => ({
        ...lease,
        agent: agentByMachineId.get(lease.machineId) ?? {
          reachability: "unknown" as const,
          lastSeenAt: null,
        },
        connection: await call(
          `/internal/leases/${segment(lease.id)}/connection`,
        ),
      })),
    );
  });
}
