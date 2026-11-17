import { machineApi, segment, text } from "@/lib/machine-api";

export async function GET(
  request: Request,
  { params }: { params: Promise<{ machineId: string; commandId: string }> },
) {
  const { machineId, commandId } = await params;
  return machineApi(request, true, async (_actor, call) =>
    call(
      `/internal/machines/${segment(machineId)}/pairing/${segment(text(commandId))}`,
    ),
  );
}
