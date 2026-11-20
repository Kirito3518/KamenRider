import { machineApi, segment, text } from "@/lib/machine-api";
import type { SunshineCommandStatus } from "@/lib/pc-control";

export async function GET(
  request: Request,
  { params }: { params: Promise<{ machineId: string; commandId: string }> },
) {
  const { machineId, commandId } = await params;
  return machineApi(request, true, async (_actor, call) => {
    const status = await call<SunshineCommandStatus>(
      `/internal/admin/pc-control/${segment(machineId)}/sunshine/${segment(text(commandId))}`,
    );
    if (!status || typeof status.commandId !== "string" || typeof status.status !== "string")
      throw new Error("Controller returned an invalid Sunshine command status");
    return status;
  });
}
