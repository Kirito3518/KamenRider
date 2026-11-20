import { guardApi, noStoreJson } from "@/lib/api-guard";
import { topupDatabase } from "@/lib/topup/db";
import { topupError } from "@/lib/topup/http";
import { listAdminTopups } from "@/lib/topup/history";

export async function GET(request: Request) {
  try {
    await guardApi(request, { admin: true });
    const database = await topupDatabase();
    const url = new URL(request.url);
    const raw: Record<string, string | undefined> = {};
    for (const [key, value] of url.searchParams.entries()) raw[key] = value;
    return noStoreJson(listAdminTopups(database, raw));
  } catch (error) {
    return topupError(error);
  }
}
