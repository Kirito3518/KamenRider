import {
  guardApi,
  noStoreJson,
  readJsonObject,
  rejectUnexpectedFields,
} from "@/lib/api-guard";
import { topupDatabase } from "@/lib/topup/db";
import { topupError } from "@/lib/topup/http";
import { createTrueMoneyTopup } from "@/lib/topup/services/truemoney-service";

export async function POST(request: Request) {
  try {
    const actor = await guardApi(request);
    const body = await readJsonObject(request);
    rejectUnexpectedFields(body, ["requestId", "link"]);
    const database = await topupDatabase();
    return noStoreJson(
      await createTrueMoneyTopup(database, actor, {
        requestId: body.requestId,
        link: body.link,
      }),
    );
  } catch (error) {
    return topupError(error);
  }
}
