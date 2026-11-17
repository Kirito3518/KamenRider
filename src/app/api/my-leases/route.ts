import { machineApi, segment } from "@/lib/machine-api";
import type { Lease, Machine } from "@/lib/machine-contracts";

type LeaseConnection = { address: string | null; stale: boolean };

const unavailableConnection: LeaseConnection = { address: null, stale: false };

/**
 * The Controller returns a verified `publicIp:moonlightPort` address only while
 * the lease is active. Rebuild the known shape defensively so unexpected
 * Controller fields can never reach the renter's browser.
 */
function connectionFromController(value: unknown): LeaseConnection {
  if (!value || typeof value !== "object" || Array.isArray(value))
    return unavailableConnection;
  const record = value as Record<string, unknown>;
  return {
    address:
      typeof record.address === "string" && record.address.trim()
        ? record.address
        : null,
    stale: record.stale === true,
  };
}

export function GET(r: Request) {
  return machineApi(r, false, async (actor, call) => {
    const [leases, machines] = await Promise.all([
      call<Lease[]>(`/internal/users/${segment(actor.id)}/leases`),
      call<Machine[]>("/internal/machines"),
    ]);
    const machineById = new Map(
      machines.map((machine) => [
        machine.machineId,
        {
          displayName: machine.displayName,
          agent: {
            reachability:
              machine.readiness === "unreachable"
                ? "unreachable"
                : machine.readiness === "ready" ||
                    machine.readiness === "notReady"
                  ? "reachable"
                  : "unknown",
            lastSeenAt: machine.lastSeenAt,
          } as const,
        },
      ]),
    );

    return Promise.all(
      leases.map(async (lease) => {
        const machine = machineById.get(lease.machineId);
        return {
          ...lease,
          // Agent-reported display name (hostname by default); safe to show the
          // renter because it is already published on the public machine list.
          machineDisplayName: machine?.displayName ?? null,
          agent: machine?.agent ?? {
            reachability: "unknown" as const,
            lastSeenAt: null,
          },
          // Only the owner sees this route and only an active lease has a
          // verified connect address; do not attach one to any other state.
          connection:
            lease.state === "active"
              ? connectionFromController(
                  await call<unknown>(
                    `/internal/leases/${segment(lease.id)}/connection`,
                  ),
                )
              : unavailableConnection,
        };
      }),
    );
  });
}
