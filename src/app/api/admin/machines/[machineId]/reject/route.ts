import { machineApi, segment } from "@/lib/machine-api";
export async function POST(
  r: Request,
  { params }: { params: Promise<{ machineId: string }> },
) {
  const { machineId } = await params;
  return machineApi(r, true, async (_, call) =>
    call(`/internal/machines/${segment(machineId)}/reject`, {
      method: "POST",
      body: JSON.stringify(await r.json()),
    }),
  );
}
