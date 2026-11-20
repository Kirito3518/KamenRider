import {
  guardApi,
  noStoreJson,
  readJsonObject,
} from "@/lib/api-guard";
import { topupDatabase } from "@/lib/topup/db";
import { topupError } from "@/lib/topup/http";
import {
  getAdminTopupSettings,
  updateTopupSettings,
} from "@/lib/topup/settings";

export async function GET(request: Request) {
  try {
    await guardApi(request, { admin: true });
    const database = await topupDatabase();
    return noStoreJson(getAdminTopupSettings(database));
  } catch (error) {
    return topupError(error);
  }
}

export async function PUT(request: Request) {
  try {
    const actor = await guardApi(request, { admin: true });
    const body = await readJsonObject(request);
    const database = await topupDatabase();
    return noStoreJson(updateTopupSettings(database, actor, body));
  } catch (error) {
    return topupError(error);
  }
}
