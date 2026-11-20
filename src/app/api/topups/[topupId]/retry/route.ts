import { guardApi, noStoreJson } from "@/lib/api-guard";
import { topupDatabase } from "@/lib/topup/db";
import { topupError } from "@/lib/topup/http";
import { TopupError } from "@/lib/topup/errors";
import { requireOwnedTopup } from "@/lib/topup/services/shared";
import { retrySlipVerification } from "@/lib/topup/services/slip-service";
import { retryTrueMoneyTopup } from "@/lib/topup/services/truemoney-service";

export async function POST(
  request: Request,
  { params }: { params: Promise<{ topupId: string }> },
) {
  try {
    const actor = await guardApi(request);
    const { topupId } = await params;
    const database = await topupDatabase();
    const row = requireOwnedTopup(database, topupId, actor.id);
    if (row.channel === "truemoney") {
      return noStoreJson(
        await retryTrueMoneyTopup(database, actor, topupId),
      );
    }
    if (row.channel === "promptpay" || row.channel === "bank_transfer") {
      return noStoreJson(
        await retrySlipVerification(database, actor, topupId),
      );
    }
    throw new TopupError(
      "invalid-request",
      "รายการนี้ไม่รองรับการลองใหม่",
      400,
    );
  } catch (error) {
    return topupError(error);
  }
}
