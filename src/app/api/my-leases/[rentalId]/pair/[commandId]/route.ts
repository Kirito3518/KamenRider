import { machineApi, segment } from "@/lib/machine-api";
export async function GET(
  r: Request,
  { params }: { params: Promise<{ rentalId: string; commandId: string }> },
) {
  const { rentalId, commandId } = await params;
  return machineApi(r, false, (_, call) =>
    call(`/internal/leases/${segment(rentalId)}/pair/${segment(commandId)}`),
  );
}
