import { ControllerError } from "@/lib/controller-client";
import { integer, machineApi, segment } from "@/lib/machine-api";

type RecoveryContext = {
  machineId: string;
  chainId: string;
  currentHeadOperationId: string;
  currentGeneration: number;
  currentStatus: string;
  currentTargetVersion: string;
  held: true;
  allocationHeld: boolean;
};

function recoveryIdentifier(value: unknown): string {
  if (
    typeof value !== "string" ||
    !value.trim() ||
    value.length > 128 ||
    /[\u0000-\u001f\u007f]/.test(value)
  )
    throw new ControllerError(503);
  return value;
}

function controllerRecoveryContext(
  value: unknown,
  machineId: string,
): RecoveryContext {
  if (typeof value !== "object" || value === null || Array.isArray(value))
    throw new ControllerError(503);
  const item = value as Record<string, unknown>;
  if (
    item.machineId !== machineId ||
    item.held !== true ||
    typeof item.allocationHeld !== "boolean" ||
    typeof item.currentStatus !== "string" ||
    !item.currentStatus.trim() ||
    item.currentStatus.length > 64 ||
    /[\u0000-\u001f\u007f]/.test(item.currentStatus) ||
    typeof item.currentTargetVersion !== "string" ||
    !item.currentTargetVersion.trim() ||
    item.currentTargetVersion.length > 128 ||
    /[\u0000-\u001f\u007f]/.test(item.currentTargetVersion)
  )
    throw new ControllerError(503);

  try {
    return {
      machineId,
      chainId: recoveryIdentifier(item.chainId),
      currentHeadOperationId: recoveryIdentifier(item.currentHeadOperationId),
      currentGeneration: integer(item.currentGeneration, 1),
      currentStatus: item.currentStatus,
      currentTargetVersion: item.currentTargetVersion,
      held: true,
      allocationHeld: item.allocationHeld,
    };
  } catch {
    throw new ControllerError(503);
  }
}

function recoveryContextError(error: unknown): Response | null {
  if (!(error instanceof ControllerError)) return null;
  const headers = { "Cache-Control": "no-store" };
  if (error.status === 404)
    return Response.json(
      { available: false, error: "No held maintenance chain is available for recovery." },
      { status: 404, headers },
    );
  if (error.status === 503)
    return Response.json(
      {
        available: false,
        error:
          "Recovery context is unavailable while the Controller is quarantined or recovery is disabled.",
      },
      { status: 503, headers },
    );
  return null;
}

export async function GET(
  request: Request,
  { params }: { params: Promise<{ machineId: string }> },
) {
  const { machineId } = await params;
  return machineApi(
    request,
    true,
    async (_actor, call) => {
      const requestedMachineId = recoveryIdentifier(machineId);
      const path = `/internal/machines/${segment(requestedMachineId)}/agent-update-recovery-context`;
      const context = controllerRecoveryContext(
        await call<unknown>(path),
        requestedMachineId,
      );
      return { available: true, context };
    },
    false,
    recoveryContextError,
  );
}
