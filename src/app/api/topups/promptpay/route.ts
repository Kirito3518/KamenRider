import {
  guardApi,
  noStoreJson,
  readJsonObject,
  rejectUnexpectedFields,
} from "@/lib/api-guard";
import { topupDatabase } from "@/lib/topup/db";
import { topupError } from "@/lib/topup/http";
import { createPromptpayTopup } from "@/lib/topup/services/promptpay-service";

export async function POST(request: Request) {
  try {
    const actor = await guardApi(request);
    const body = await readJsonObject(request);
    rejectUnexpectedFields(body, ["requestId", "amountSatang"]);
    const database = await topupDatabase();
    return noStoreJson(
      await createPromptpayTopup(database, actor, {
        requestId: body.requestId,
        amountSatang: body.amountSatang,
      }),
    );
  } catch (error) {
    return topupError(error);
  }
}
