import {
  guardApi,
  noStoreJson,
  readJsonObject,
  rejectUnexpectedFields,
} from "@/lib/api-guard";
import { topupDatabase } from "@/lib/topup/db";
import { topupError } from "@/lib/topup/http";
import { createBankTransferTopup } from "@/lib/topup/services/bank-service";

export async function POST(request: Request) {
  try {
    const actor = await guardApi(request);
    const body = await readJsonObject(request);
    rejectUnexpectedFields(body, ["requestId", "amountSatang", "bankAccountId"]);
    const database = await topupDatabase();
    return noStoreJson(
      createBankTransferTopup(database, actor, {
        requestId: body.requestId,
        amountSatang: body.amountSatang,
        bankAccountId: body.bankAccountId,
      }),
    );
  } catch (error) {
    return topupError(error);
  }
}
