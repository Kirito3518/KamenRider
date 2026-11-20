import {
  guardApi,
  noStoreJson,
  readJsonObject,
} from "@/lib/api-guard";
import { topupDatabase } from "@/lib/topup/db";
import { topupError } from "@/lib/topup/http";
import { isSlipProvider } from "@/lib/topup/constants";
import { TopupError } from "@/lib/topup/errors";
import { updateTopupProvider } from "@/lib/topup/settings";

export async function PUT(
  request: Request,
  { params }: { params: Promise<{ provider: string }> },
) {
  try {
    const actor = await guardApi(request, { admin: true });
    const { provider } = await params;
    if (!isSlipProvider(provider)) {
      throw new TopupError("invalid-request", "ผู้ให้บริการไม่ถูกต้อง", 400);
    }
    const body = await readJsonObject(request);
    const database = await topupDatabase();
    return noStoreJson(updateTopupProvider(database, actor, provider, body));
  } catch (error) {
    return topupError(error);
  }
}
