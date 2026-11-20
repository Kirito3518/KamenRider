import { guardApi, noStoreJson } from "@/lib/api-guard";
import { topupDatabase } from "@/lib/topup/db";
import { topupError } from "@/lib/topup/http";
import { getAdminTopupDetail } from "@/lib/topup/history";

export async function GET(
  request: Request,
  { params }: { params: Promise<{ topupId: string }> },
) {
  try {
    await guardApi(request, { admin: true });
    const { topupId } = await params;
    const database = await topupDatabase();
    return noStoreJson({ topup: getAdminTopupDetail(database, topupId) });
  } catch (error) {
    return topupError(error);
  }
}
