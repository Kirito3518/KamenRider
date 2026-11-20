import { randomBytes } from "node:crypto";

/** Short, human-readable top-up reference: TP + Bangkok date + random hex. */
export function createTopupReference(now = Date.now()): string {
  const date = new Date(now + 7 * 60 * 60 * 1_000)
    .toISOString()
    .slice(0, 10)
    .replace(/-/g, "");
  const random = randomBytes(4).toString("hex").toUpperCase();
  return `TP${date}${random}`;
}

export function createProcessingToken(): string {
  return randomBytes(24).toString("base64url");
}
