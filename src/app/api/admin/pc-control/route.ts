import { getAuth } from "@/lib/auth";
import { machineApi } from "@/lib/machine-api";
import type { PcControlSnapshot } from "@/lib/pc-control";

type ControllerLease = {
  rentalId: string;
  userId: string;
  state: string;
  revision: number;
  pendingRevision: number | null;
  startsAtUtc: string | null;
  expiresAtUtc: string | null;
  remainingSeconds: number | null;
};

type ControllerSnapshot = {
  serverTimeUtc: string;
  machines: { machine: PcControlSnapshot["machines"][number]; lease: ControllerLease | null }[];
};

type UserRow = { id: string; name: string | null; email: string | null };

export function GET(request: Request) {
  return machineApi(request, true, async (_actor, call) => {
    const snapshot = await call<ControllerSnapshot>("/internal/pc-control");
    if (!snapshot || !Array.isArray(snapshot.machines) || typeof snapshot.serverTimeUtc !== "string")
      throw new Error("Controller returned an invalid PC-control snapshot");

    const userIds = [
      ...new Set(
        snapshot.machines
          .map((entry) => entry.lease?.userId)
          .filter((value): value is string => typeof value === "string" && value.length > 0),
      ),
    ];
    const users = new Map<string, UserRow>();
    if (userIds.length) {
      const database = (await getAuth()).options.database;
      const placeholders = userIds.map(() => "?").join(",");
      const rows = database
        .prepare(`SELECT id, name, email FROM user WHERE id IN (${placeholders})`)
        .all(...userIds) as UserRow[];
      for (const row of rows) users.set(row.id, row);
    }

    const machines = snapshot.machines.map(({ machine, lease }) => {
      if (!lease) return { ...machine, lease: null };
      const user = users.get(lease.userId);
      return {
        ...machine,
        lease: {
          ...lease,
          userName: user?.name?.trim() || lease.userId,
          userEmail: user?.email ?? "",
        },
      };
    });

    return { serverTimeUtc: snapshot.serverTimeUtc, machines } satisfies PcControlSnapshot;
  });
}
