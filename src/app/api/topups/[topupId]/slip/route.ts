import { guardApi, noStoreJson } from "@/lib/api-guard";
import { topupDatabase } from "@/lib/topup/db";
import { topupError } from "@/lib/topup/http";
import { MAX_SLIP_BYTES } from "@/lib/topup/constants";
import { readSlipFile } from "@/lib/topup/files";
import { submitSlip } from "@/lib/topup/services/slip-service";
import { requireTopupRow } from "@/lib/topup/transactions";

export async function GET(
  request: Request,
  { params }: { params: Promise<{ topupId: string }> },
) {
  try {
    const actor = await guardApi(request);
    const { topupId } = await params;
    const database = await topupDatabase();
    const row = requireTopupRow(database, topupId);
    if (row.user_id !== actor.id && actor.role !== "admin") {
      return noStoreJson({ error: "ไม่พบรูปสลิป", code: "transaction-not-found" }, 404);
    }
    if (!row.slip_image_path || !row.slip_image_mime) {
      return noStoreJson({ error: "ไม่พบรูปสลิป", code: "transaction-not-found" }, 404);
    }
    const served = readSlipFile(row.slip_image_path, row.slip_image_mime);
    const copy = new Uint8Array(served.bytes.byteLength);
    copy.set(served.bytes);
    return new Response(copy.buffer, {
      headers: {
        "Content-Type": served.mime,
        "Content-Disposition": `inline; filename="slip-${row.reference}.${served.extension}"`,
        "Cache-Control": "private, no-store",
        "X-Content-Type-Options": "nosniff",
      },
    });
  } catch (error) {
    return topupError(error);
  }
}

export async function POST(
  request: Request,
  { params }: { params: Promise<{ topupId: string }> },
) {
  try {
    const actor = await guardApi(request);
    const contentLength = Number(request.headers.get("content-length") || 0);
    if (contentLength > MAX_SLIP_BYTES + 1_000_000) {
      return noStoreJson(
        { error: "รูปสลิปต้องมีขนาดไม่เกิน 4 MB", code: "file-too-large" },
        413,
      );
    }
    const { topupId } = await params;
    let formData: FormData;
    try {
      formData = await request.formData();
    } catch {
      return noStoreJson(
        { error: "ข้อมูลอัปโหลดไม่ถูกต้อง", code: "invalid-request" },
        400,
      );
    }
    const database = await topupDatabase();
    const result = await submitSlip(database, actor, topupId, formData);
    return noStoreJson(result);
  } catch (error) {
    return topupError(error);
  }
}
