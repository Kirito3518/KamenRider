import { machineApi, segment, text } from "@/lib/machine-api";

export async function POST(
  request: Request,
  { params }: { params: Promise<{ machineId: string }> },
) {
  const { machineId } = await params;
  return machineApi(request, true, async (_actor, call) => {
    const body: unknown = await request.json();
    if (!body || typeof body !== "object" || Array.isArray(body)) throw new TypeError("Invalid request");
    const record = body as Record<string, unknown>;
    if (Object.keys(record).some((key) => key !== "requestId")) throw new TypeError("Unexpected request field");
    return call(`/internal/machines/${segment(machineId)}/power-off`, {
      method: "POST",
      body: JSON.stringify({ requestId: text(record.requestId) }),
    });
  });
}
