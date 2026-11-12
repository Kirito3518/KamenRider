import { machineApi } from "@/lib/machine-api";
import { ControllerError } from "@/lib/controller-client";

export async function POST(request: Request) {
  return machineApi(request, true, async () => {
    // AgentMaintenance start/bind-source/report/reconcile are not all implemented.
    // Never issue a command that could be mistaken for a production-ready update.
    throw new ControllerError(503);
  });
}
