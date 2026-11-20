import { machineApi, segment, text } from "@/lib/machine-api";
import type { SunshineCommandStatus } from "@/lib/pc-control";

const actions = new Set(["restart", "unpair"]);

/**
 * Queues a machine-scoped Sunshine maintenance command through the internal Controller.
 * The browser never picks the target boot: the Controller binds the command to the Agent boot
 * that is currently heartbeating for this machine.
 */
export async function POST(
  request: Request,
  { params }: { params: Promise<{ machineId: string }> },
) {
  const { machineId } = await params;
  return machineApi(request, true, async (_actor, call) => {
    const body: unknown = await request.json();
    if (!body || typeof body !== "object" || Array.isArray(body)) throw new TypeError("Invalid request");
    const record = body as Record<string, unknown>;
    if (Object.keys(record).some((key) => !["action", "force", "requestId"].includes(key)))
      throw new TypeError("Unexpected request field");
    if (typeof record.action !== "string" || !actions.has(record.action)) throw new TypeError("Invalid action");
    if (typeof record.force !== "boolean") throw new TypeError("Invalid force");
    const requestId = record.requestId === undefined ? undefined : text(record.requestId);
    const created = await call<SunshineCommandStatus>(
      `/internal/admin/pc-control/${segment(machineId)}/sunshine`,
      {
        method: "POST",
        body: JSON.stringify({
          action: record.action,
          force: record.force,
          ...(requestId === undefined ? {} : { requestId }),
        }),
      },
    );
    if (!created || typeof created.commandId !== "string" || typeof created.status !== "string")
      throw new Error("Controller returned an invalid Sunshine command result");
    return created;
  });
}
