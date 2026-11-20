import {
  guardApi,
  noStoreJson,
  readJsonObject,
} from "@/lib/api-guard";
import { topupDatabase } from "@/lib/topup/db";
import { topupError } from "@/lib/topup/http";
import {
  createBankAccount,
  listBankAccounts,
} from "@/lib/topup/settings";

export async function GET(request: Request) {
  try {
    await guardApi(request, { admin: true });
    const database = await topupDatabase();
    return noStoreJson({ bankAccounts: listBankAccounts(database, true) });
  } catch (error) {
    return topupError(error);
  }
}

export async function POST(request: Request) {
  try {
    const actor = await guardApi(request, { admin: true });
    const body = await readJsonObject(request, 200 * 1024);
    const database = await topupDatabase();
    return noStoreJson({ bankAccount: createBankAccount(database, actor, body) });
  } catch (error) {
    return topupError(error);
  }
}
