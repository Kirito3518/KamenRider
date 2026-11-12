import { machineApi, integer, segment } from "@/lib/machine-api";
import { ControllerError, type SafeRecoveryHead } from "@/lib/controller-client";

const controllerPath = (machineId: string) =>
  `/internal/machines/${segment(machineId)}/agent-update-recovery-intents`;
const maxRecoveryRequestBytes = 8 * 1024;

type RecoveryIntent = {
  intentId: string;
  machineId: string;
  chainId: string;
  previousHeadOperationId: string;
  expectedGeneration: number;
  generation: number;
  targetReleaseVersion: string;
  targetVersion: string;
  targetRuntime: string;
  reason: string;
  requestId: string;
  status: string;
  held: boolean;
  createdAtUtc: string;
  expiresAtUtc: string;
};

type RecoveryIntentBinding = {
  machineId: string;
  actorId: string;
  chainId?: string;
  headId?: string;
  expectedGeneration?: number;
  targetReleaseVersion?: string;
  requestId?: string;
};

function recoveryIdentifier(value: unknown) {
  if (
    typeof value !== "string" ||
    !value.trim() ||
    value.length > 128 ||
    /[\u0000-\u001f\u007f]/.test(value)
  )
    throw new TypeError("Invalid request");
  return value;
}

async function readJsonBody(request: Request): Promise<unknown> {
  const contentLength = request.headers.get("content-length");
  if (contentLength !== null) {
    const declaredLength = Number(contentLength);
    if (
      !Number.isSafeInteger(declaredLength) ||
      declaredLength < 0 ||
      declaredLength > maxRecoveryRequestBytes
    )
      throw new TypeError("Invalid request");
  }

  const reader = request.body?.getReader();
  if (!reader) throw new TypeError("Invalid request");
  const chunks: Uint8Array[] = [];
  let totalBytes = 0;
  while (true) {
    const { done, value } = await reader.read();
    if (done) break;
    totalBytes += value.byteLength;
    if (totalBytes > maxRecoveryRequestBytes) {
      await reader.cancel().catch(() => {});
      throw new TypeError("Invalid request");
    }
    chunks.push(value);
  }

  const bytes = new Uint8Array(totalBytes);
  let offset = 0;
  for (const chunk of chunks) {
    bytes.set(chunk, offset);
    offset += chunk.byteLength;
  }
  return JSON.parse(new TextDecoder("utf-8", { fatal: true }).decode(bytes));
}

function controllerIntent(
  value: unknown,
  binding: RecoveryIntentBinding,
): RecoveryIntent {
  if (typeof value !== "object" || value === null || Array.isArray(value))
    throw new ControllerError(503);
  const item = value as Record<string, unknown>;
  const stringFields = [
    "intentId",
    "machineId",
    "chainId",
    "previousHeadOperationId",
    "targetReleaseVersion",
    "targetVersion",
    "targetRuntime",
    "actorId",
    "requestId",
    "status",
  ] as const;
  if (
    stringFields.some(
      (field) =>
        typeof item[field] !== "string" ||
        !(item[field] as string).trim() ||
        item[field].length > 128 ||
        /[\u0000-\u001f\u007f]/.test(item[field] as string),
    ) ||
    !Number.isSafeInteger(item.expectedGeneration) ||
    Number(item.expectedGeneration) <= 0 ||
    !Number.isSafeInteger(item.generation) ||
    Number(item.generation) !== Number(item.expectedGeneration) + 1 ||
    typeof item.held !== "boolean" ||
    typeof item.reason !== "string" ||
    !item.reason.trim() ||
    item.reason.length > 512 ||
    /[\u0000-\u001f\u007f]/.test(item.reason) ||
    typeof item.createdAtUtc !== "string" ||
    item.createdAtUtc.length > 64 ||
    !Number.isFinite(Date.parse(String(item.createdAtUtc))) ||
    typeof item.expiresAtUtc !== "string" ||
    item.expiresAtUtc.length > 64 ||
    !Number.isFinite(Date.parse(String(item.expiresAtUtc)))
  )
    throw new ControllerError(503);

  if (
    item.machineId !== binding.machineId ||
    item.actorId !== binding.actorId ||
    (binding.chainId !== undefined && item.chainId !== binding.chainId) ||
    (binding.headId !== undefined &&
      item.previousHeadOperationId !== binding.headId) ||
    (binding.expectedGeneration !== undefined &&
      item.expectedGeneration !== binding.expectedGeneration) ||
    (binding.targetReleaseVersion !== undefined &&
      item.targetReleaseVersion !== binding.targetReleaseVersion) ||
    (binding.requestId !== undefined && item.requestId !== binding.requestId)
  )
    throw new ControllerError(503);

  return {
    intentId: item.intentId as string,
    machineId: item.machineId as string,
    chainId: item.chainId as string,
    previousHeadOperationId: item.previousHeadOperationId as string,
    expectedGeneration: Number(item.expectedGeneration),
    generation: Number(item.generation),
    targetReleaseVersion: item.targetReleaseVersion as string,
    targetVersion: item.targetVersion as string,
    targetRuntime: item.targetRuntime as string,
    reason: item.reason as string,
    requestId: item.requestId as string,
    status: item.status as string,
    held: item.held,
    createdAtUtc: item.createdAtUtc as string,
    expiresAtUtc: item.expiresAtUtc as string,
  };
}

function validateCreateBody(value: unknown) {
  if (typeof value !== "object" || value === null || Array.isArray(value))
    throw new TypeError("Invalid request");
  const body = value as Record<string, unknown>;
  const keys = Object.keys(body).sort();
  const expectedKeys = [
    "expectedChainId",
    "expectedGeneration",
    "expectedHeadId",
    "reason",
    "requestId",
    "targetReleaseVersion",
  ].sort();
  if (
    keys.length !== expectedKeys.length ||
    keys.some((key, index) => key !== expectedKeys[index])
  )
    throw new TypeError("Invalid request");

  const reason = body.reason;
  if (
    typeof reason !== "string" ||
    !reason.trim() ||
    reason.length > 512 ||
    /[\u0000-\u001f\u007f]/.test(reason)
  )
    throw new TypeError("Invalid request");

  return {
    expectedChainId: recoveryIdentifier(body.expectedChainId),
    expectedHeadId: recoveryIdentifier(body.expectedHeadId),
    expectedGeneration: integer(body.expectedGeneration, 1),
    targetReleaseVersion: recoveryIdentifier(body.targetReleaseVersion),
    reason,
    requestId: recoveryIdentifier(body.requestId),
  };
}

function recoveryErrorResponse(error: unknown): Response | null {
  if (!(error instanceof ControllerError)) return null;
  const headers = { "Cache-Control": "no-store" };
  const safeRecoveryHead: SafeRecoveryHead | null = error.safeRecoveryHead;

  if (safeRecoveryHead)
    return Response.json(
      {
        available: false,
        pending: false,
        error: "The maintenance head changed. Refresh the current state before retrying.",
        currentHead: safeRecoveryHead,
      },
      { status: 409, headers },
    );
  if (error.safeReleaseReason)
    return Response.json(
      {
        available: false,
        pending: false,
        error: "The requested recovery release is not available.",
        reason: error.safeReleaseReason,
      },
      { status: 409, headers },
    );
  if (error.status === 404)
    return Response.json(
      {
        available: false,
        pending: false,
        error: "The held maintenance chain or recovery intent was not found.",
      },
      { status: 404, headers },
    );
  if (error.status === 409)
    return Response.json(
      {
        available: false,
        pending: false,
        error: "A recovery intent is not available for the current machine state.",
      },
      { status: 409, headers },
    );
  if (error.status === 503)
    return Response.json(
      {
        available: false,
        pending: false,
        error: "Recovery intents are not available right now.",
      },
      { status: 503, headers },
    );
  return Response.json(
    { available: false, pending: false, error: "Recovery intent request rejected." },
    { status: error.status, headers },
  );
}

function isPendingRecoveryIntent(intent: RecoveryIntent) {
  return (
    intent.held &&
    intent.status === "awaitingInspection" &&
    Date.parse(intent.expiresAtUtc) > Date.now()
  );
}

export async function POST(
  request: Request,
  { params }: { params: Promise<{ machineId: string }> },
) {
  const { machineId } = await params;
  return machineApi(
    request,
    true,
    async (actor, call) => {
      const body = validateCreateBody(await readJsonBody(request));
      const requestedMachineId = recoveryIdentifier(machineId);
      const intent = controllerIntent(
        await call<unknown>(controllerPath(requestedMachineId), {
          method: "POST",
          body: JSON.stringify(body),
        }),
        {
          machineId: requestedMachineId,
          actorId: actor.id,
          chainId: body.expectedChainId,
          headId: body.expectedHeadId,
          expectedGeneration: body.expectedGeneration,
          targetReleaseVersion: body.targetReleaseVersion,
          requestId: body.requestId,
        },
      );
      return {
        available: true,
        pending: isPendingRecoveryIntent(intent),
        intent,
      };
    },
    false,
    recoveryErrorResponse,
  );
}

export async function GET(
  request: Request,
  { params }: { params: Promise<{ machineId: string }> },
) {
  const { machineId } = await params;
  return machineApi(
    request,
    true,
    async (actor, call) => {
      const url = new URL(request.url);
      const entries = [...url.searchParams.entries()];
      if (
        entries.length !== 1 ||
        entries[0][0] !== "requestId" ||
        !url.searchParams.has("requestId")
      )
        throw new TypeError("Invalid request");
      const requestId = recoveryIdentifier(entries[0][1]);
      const requestedMachineId = recoveryIdentifier(machineId);
      const path = `${controllerPath(requestedMachineId)}?${new URLSearchParams({ requestId })}`;
      const intent = controllerIntent(await call<unknown>(path), {
        machineId: requestedMachineId,
        actorId: actor.id,
        requestId,
      });
      return {
        available: true,
        pending: isPendingRecoveryIntent(intent),
        intent,
      };
    },
    false,
    recoveryErrorResponse,
  );
}
