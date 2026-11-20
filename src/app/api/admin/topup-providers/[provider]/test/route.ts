import { guardApi, noStoreJson } from "@/lib/api-guard";
import { topupDatabase } from "@/lib/topup/db";
import { topupError } from "@/lib/topup/http";
import { isSlipProvider } from "@/lib/topup/constants";
import { TopupError } from "@/lib/topup/errors";
import { testTopupProvider } from "@/lib/topup/settings";

export async function POST(
  request: Request,
  { params }: { params: Promise<{ provider: string }> },
) {
  try {
    const actor = await guardApi(request, { admin: true });
    const { provider } = await params;
    if (!isSlipProvider(provider)) {
      throw new TopupError("invalid-request", "ผู้ให้บริการไม่ถูกต้อง", 400);
    }
    const database = await topupDatabase();
    return noStoreJson(await testTopupProvider(database, actor, provider));
  } catch (error) {
    return topupError(error);
  }
}
