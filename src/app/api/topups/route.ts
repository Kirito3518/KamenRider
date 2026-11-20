import { guardApi, noStoreJson } from "@/lib/api-guard";
import { topupDatabase } from "@/lib/topup/db";
import { listCustomerTopups } from "@/lib/topup/history";
import { topupError } from "@/lib/topup/http";
import { expireStaleTopups } from "@/lib/topup/transactions";
import { readUserBalance } from "@/lib/topup/services/shared";

export async function GET(request: Request) {
  try {
    const actor = await guardApi(request);
    const database = await topupDatabase();
    expireStaleTopups(database);
    const url = new URL(request.url);
    const raw: Record<string, string | undefined> = {};
    for (const [key, value] of url.searchParams.entries()) raw[key] = value;
    const data = listCustomerTopups(database, actor.id, raw);
    return noStoreJson({
      ...data,
      balanceSatang: readUserBalance(database, actor.id),
    });
  } catch (error) {
    return topupError(error);
  }
}
