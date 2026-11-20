import { guardApi, noStoreJson } from "@/lib/api-guard";
import { topupDatabase } from "@/lib/topup/db";
import { getPublicTopupConfig } from "@/lib/topup/settings";
import { topupError } from "@/lib/topup/http";

export async function GET(request: Request) {
  try {
    await guardApi(request);
    const database = await topupDatabase();
    return noStoreJson(getPublicTopupConfig(database));
  } catch (error) {
    return topupError(error);
  }
}
