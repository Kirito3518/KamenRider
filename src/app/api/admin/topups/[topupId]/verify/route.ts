import { guardApi, noStoreJson } from "@/lib/api-guard";
import { topupDatabase } from "@/lib/topup/db";
import { topupError } from "@/lib/topup/http";
import { adminRetrySlipVerification } from "@/lib/topup/services/slip-service";

export async function POST(
  request: Request,
  { params }: { params: Promise<{ topupId: string }> },
) {
  try {
    const actor = await guardApi(request, { admin: true });
    const { topupId } = await params;
    const database = await topupDatabase();
    return noStoreJson(
      await adminRetrySlipVerification(database, actor, topupId),
    );
  } catch (error) {
    return topupError(error);
  }
}
