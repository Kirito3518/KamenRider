import "server-only";
import { controllerRequest, ControllerError } from "@/lib/controller-client";
import type { Machine } from "@/lib/machine-contracts";

/**
 * Server-side Controller machine snapshot for the admin overview.
 *
 * The Controller is an optional dependency: when it is not configured or not
 * reachable the overview still renders its money sections, and only the machine
 * section falls back to an unavailable state. Raw `Machine` rows stay on the
 * server; the dashboard only forwards summarized counts to client components.
 */

export type MachineSnapshot = {
  available: boolean;
  machines: Machine[];
  readAt: number;
};

export type MachineActor = { id: string; role: string };

export async function readMachinesSnapshot(actor: MachineActor): Promise<MachineSnapshot> {
  const readAt = Date.now();
  try {
    const machines = await controllerRequest<Machine[]>("/internal/machines", {
      headers: {
        "X-Authenticated-Actor-Id": actor.id,
        "X-Authenticated-Actor-Role": actor.role,
      },
    });
    return {
      available: true,
      machines: Array.isArray(machines) ? machines : [],
      readAt,
    };
  } catch (error) {
    if (error instanceof ControllerError) {
      return { available: false, machines: [], readAt };
    }
    throw error;
  }
}
