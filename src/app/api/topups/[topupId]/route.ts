import { guardApi, noStoreJson } from "@/lib/api-guard";
import { topupDatabase } from "@/lib/topup/db";
import { topupError } from "@/lib/topup/http";
import { getCustomerTopupDetail } from "@/lib/topup/history";
import { expireStaleTopups } from "@/lib/topup/transactions";
import { readUserBalance } from "@/lib/topup/services/shared";

export async function GET(
  request: Request,
  { params }: { params: Promise<{ topupId: string }> },
) {
  try {
    const actor = await guardApi(request);
    const { topupId } = await params;
    const database = await topupDatabase();
    expireStaleTopups(database);
    const topup = getCustomerTopupDetail(database, actor.id, topupId);
    return noStoreJson({
      topup,
      balanceSatang: readUserBalance(database, actor.id),
    });
  } catch (error) {
    return topupError(error);
  }
}
