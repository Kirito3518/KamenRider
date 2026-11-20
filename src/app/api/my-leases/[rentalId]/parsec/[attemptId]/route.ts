import { machineApi, segment } from "@/lib/machine-api";

export async function GET(
  r: Request,
  { params }: { params: Promise<{ rentalId: string; attemptId: string }> },
) {
  const { rentalId, attemptId } = await params;
  return machineApi(r, false, (_, call) =>
    call(
      `/internal/leases/${segment(rentalId)}/parsec/${segment(attemptId)}`,
    ),
  );
}
