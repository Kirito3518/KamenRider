import { guardApi, noStoreJson } from "@/lib/api-guard";
import { topupDatabase } from "@/lib/topup/db";
import { TopupError } from "@/lib/topup/errors";
import { topupError } from "@/lib/topup/http";
import { manualReviewTopup } from "@/lib/topup/services/review-service";

export async function POST(
  request: Request,
  { params }: { params: Promise<{ topupId: string }> },
) {
  try {
    const actor = await guardApi(request, { admin: true });
    const { topupId } = await params;
    const body = (await request.json().catch(() => ({}))) as Record<string, unknown>;
    if (body.action !== "approve" && body.action !== "reject") {
      throw new TopupError(
        "invalid-request",
        "คำสั่งตรวจสอบต้องเป็น approve หรือ reject",
        400,
      );
    }
    const database = await topupDatabase();
    return noStoreJson(
      manualReviewTopup(database, actor, topupId, {
        action: body.action,
        reason: typeof body.reason === "string" ? body.reason : null,
      }),
    );
  } catch (error) {
    return topupError(error);
  }
}
