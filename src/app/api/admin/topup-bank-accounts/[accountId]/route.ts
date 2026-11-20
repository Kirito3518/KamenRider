import {
  guardApi,
  noStoreJson,
  readJsonObject,
} from "@/lib/api-guard";
import { topupDatabase } from "@/lib/topup/db";
import { topupError } from "@/lib/topup/http";
import {
  archiveBankAccount,
  updateBankAccount,
} from "@/lib/topup/settings";

export async function PATCH(
  request: Request,
  { params }: { params: Promise<{ accountId: string }> },
) {
  try {
    const actor = await guardApi(request, { admin: true });
    const { accountId } = await params;
    const body = await readJsonObject(request, 200 * 1024);
    const database = await topupDatabase();
    return noStoreJson({
      bankAccount: updateBankAccount(database, actor, accountId, body),
    });
  } catch (error) {
    return topupError(error);
  }
}

export async function DELETE(
  request: Request,
  { params }: { params: Promise<{ accountId: string }> },
) {
  try {
    const actor = await guardApi(request, { admin: true });
    const { accountId } = await params;
    const database = await topupDatabase();
    return noStoreJson({
      bankAccount: archiveBankAccount(database, actor, accountId),
    });
  } catch (error) {
    return topupError(error);
  }
}
