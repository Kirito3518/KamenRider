/**
 * Slip image intake/storage. Files are never served from a public directory;
 * API routes stream them only after owner/admin authorization.
 */

import { createHash, randomUUID } from "node:crypto";
import { mkdirSync, readFileSync, unlinkSync, writeFileSync } from "node:fs";
import { basename, dirname, join, resolve } from "node:path";
import { MAX_SLIP_BYTES } from "@/lib/topup/constants";
import { TopupError } from "@/lib/topup/errors";
import {
  SLIP_MIME_TYPES,
  detectImageMime,
  extensionForMime,
} from "@/lib/topup/images";

function slipDir() {
  if (process.env.TOPUP_SLIP_DIR) return resolve(process.env.TOPUP_SLIP_DIR);
  const authDb = resolve(process.env.AUTH_DATABASE_PATH || "data/auth.sqlite");
  return join(dirname(authDb), "topup-slips");
}

function ensureSlipDir() {
  const dir = slipDir();
  mkdirSync(dir, { recursive: true, mode: 0o700 });
  return dir;
}

export type ValidatedSlipFile = {
  bytes: Uint8Array;
  mime: string;
  fileName: string;
  hash: string;
  storedName: string;
};

function formFile(value: unknown): File | null {
  if (
    value &&
    typeof value === "object" &&
    "arrayBuffer" in value &&
    "size" in value &&
    "type" in value &&
    typeof (value as File).arrayBuffer === "function"
  ) {
    return value as File;
  }
  return null;
}

export async function validateSlipFormData(
  formData: FormData,
): Promise<Omit<ValidatedSlipFile, "storedName">> {
  const file = formFile(formData.get("file"));
  if (!file) {
    throw new TopupError(
      "invalid-request",
      "กรุณาแนบไฟล์รูปสลิป",
      400,
    );
  }
  if (file.size <= 0) {
    throw new TopupError("invalid-request", "ไฟล์สลิปว่างเปล่า", 400);
  }
  if (file.size > MAX_SLIP_BYTES) {
    throw new TopupError(
      "file-too-large",
      "รูปสลิปต้องมีขนาดไม่เกิน 4 MB",
      413,
    );
  }
  const bytes = new Uint8Array(await file.arrayBuffer());
  if (bytes.byteLength > MAX_SLIP_BYTES) {
    throw new TopupError(
      "file-too-large",
      "รูปสลิปต้องมีขนาดไม่เกิน 4 MB",
      413,
    );
  }
  const mime = detectImageMime(bytes);
  if (!mime || !SLIP_MIME_TYPES.has(mime)) {
    throw new TopupError(
      "unsupported-file",
      "รองรับเฉพาะรูป JPEG, PNG หรือ WebP",
      400,
    );
  }
  return {
    bytes,
    mime,
    fileName: basename(file.name || `slip.${extensionForMime(mime)}`).slice(-80),
    hash: createHash("sha256").update(bytes).digest("hex"),
  };
}

export function saveSlipFile(
  topupId: string,
  bytes: Uint8Array,
  mime: string,
): string {
  const dir = ensureSlipDir();
  const storedName = `${topupId}-${randomUUID()}.${extensionForMime(mime)}`;
  writeFileSync(join(dir, storedName), bytes, { flag: "wx", mode: 0o600 });
  return storedName;
}

export function deleteSlipFile(storedName: string | null | undefined) {
  if (!storedName || basename(storedName) !== storedName) return;
  try {
    unlinkSync(join(slipDir(), storedName));
  } catch {
    // Best-effort cleanup only.
  }
}

export type ServedSlipFile = {
  bytes: Uint8Array;
  mime: string;
  extension: string;
};

export function readSlipFile(
  storedName: string,
  mime: string | null,
): ServedSlipFile {
  if (!storedName || basename(storedName) !== storedName) {
    throw new TopupError("transaction-not-found", "ไม่พบรูปสลิป", 404);
  }
  let bytes: Uint8Array;
  try {
    bytes = new Uint8Array(readFileSync(join(slipDir(), storedName)));
  } catch {
    throw new TopupError("transaction-not-found", "ไม่พบรูปสลิป", 404);
  }
  const detected = detectImageMime(bytes);
  if (!detected || !SLIP_MIME_TYPES.has(detected)) {
    throw new TopupError("unsupported-file", "ไฟล์สลิปไม่ถูกต้อง", 404);
  }
  const safeMime = mime && mime === detected ? mime : detected;
  return {
    bytes,
    mime: safeMime,
    extension: extensionForMime(safeMime),
  };
}

export function slipStorageDirectory() {
  return slipDir();
}
