/**
 * Server-only encryption for stored provider API keys.
 *
 * Format: `enc:v1:<iv-base64>:<tag-base64>:<ciphertext-base64>`
 *
 * The key is resolved, in order, from:
 *  1. TOPUP_SETTINGS_KEY (base64, exactly 32 bytes) — preferred for production.
 *  2. BETTER_AUTH_SECRET (SHA-256 derivation).
 *  3. `data/.auth-secret` beside the auth DB (SHA-256 derivation).
 *  4. A generated `data/.topup-secret` (0600) beside the auth DB.
 *
 * Back up the auth DB and its secret sidecar together. Rotating the encryption
 * key makes existing stored provider keys unreadable; the admin must re-enter them.
 */

import {
  createCipheriv,
  createDecipheriv,
  createHash,
  randomBytes,
} from "node:crypto";
import { mkdirSync, readFileSync, writeFileSync } from "node:fs";
import { dirname, join, resolve } from "node:path";

const PREFIX = "enc:v1:";
const AUTH_DB_PATH = resolve(
  process.env.AUTH_DATABASE_PATH || "data/auth.sqlite",
);
const DATA_DIR = dirname(AUTH_DB_PATH);

let cachedKey: Buffer | null = null;

function keyFromSecret(secret: string): Buffer {
  return createHash("sha256").update(`kamenraiderplay:topup:${secret}`).digest();
}

function readEnvKey(): Buffer | null {
  const raw = process.env.TOPUP_SETTINGS_KEY;
  if (!raw) return null;
  const bytes = Buffer.from(raw, "base64");
  if (bytes.length !== 32) {
    throw new Error("TOPUP_SETTINGS_KEY must be base64 of exactly 32 bytes");
  }
  return bytes;
}

function readSidecar(path: string): string | null {
  try {
    const value = readFileSync(path, "utf8").trim();
    return value || null;
  } catch (error) {
    if ((error as NodeJS.ErrnoException).code === "ENOENT") return null;
    throw error;
  }
}

function createSidecar(path: string): string {
  mkdirSync(dirname(path), { recursive: true });
  try {
    writeFileSync(path, randomBytes(48).toString("base64url"), {
      flag: "wx",
      mode: 0o600,
    });
  } catch (error) {
    if ((error as NodeJS.ErrnoException).code !== "EEXIST") throw error;
  }
  const value = readSidecar(path);
  if (!value) throw new Error("Unable to create top-up secret sidecar");
  return value;
}

export function getTopupSettingsKey(): Buffer {
  if (cachedKey) return cachedKey;
  const envKey = readEnvKey();
  if (envKey) {
    cachedKey = envKey;
    return envKey;
  }

  const authSecret = process.env.BETTER_AUTH_SECRET;
  if (authSecret && authSecret.trim()) {
    cachedKey = keyFromSecret(authSecret.trim());
    return cachedKey;
  }

  const authSecretPath = join(DATA_DIR, ".auth-secret");
  const existingAuthSecret = readSidecar(authSecretPath);
  if (existingAuthSecret) {
    cachedKey = keyFromSecret(existingAuthSecret);
    return cachedKey;
  }

  const topupSecretPath = join(DATA_DIR, ".topup-secret");
  const existingTopupSecret =
    readSidecar(topupSecretPath) ?? createSidecar(topupSecretPath);
  cachedKey = keyFromSecret(existingTopupSecret);
  return cachedKey;
}

export function encryptSecret(plaintext: string): string {
  if (!plaintext) throw new Error("Cannot encrypt an empty secret");
  const iv = randomBytes(12);
  const cipher = createCipheriv("aes-256-gcm", getTopupSettingsKey(), iv);
  const ciphertext = Buffer.concat([
    cipher.update(plaintext, "utf8"),
    cipher.final(),
  ]);
  const tag = cipher.getAuthTag();
  return `${PREFIX}${iv.toString("base64")}:${tag.toString("base64")}:${ciphertext.toString("base64")}`;
}

export function decryptSecret(stored: string | null | undefined): string | null {
  if (!stored) return null;
  if (!stored.startsWith(PREFIX)) return null;
  const parts = stored.slice(PREFIX.length).split(":");
  if (parts.length !== 3) return null;
  const [ivPart, tagPart, dataPart] = parts;
  try {
    const decipher = createDecipheriv(
      "aes-256-gcm",
      getTopupSettingsKey(),
      Buffer.from(ivPart, "base64"),
    );
    decipher.setAuthTag(Buffer.from(tagPart, "base64"));
    const plaintext = Buffer.concat([
      decipher.update(Buffer.from(dataPart, "base64")),
      decipher.final(),
    ]);
    return plaintext.toString("utf8");
  } catch {
    // A rotated/wrong key must not crash an admin settings read; the caller
    // treats the provider as unconfigured and asks the admin to re-enter it.
    return null;
  }
}

/** Test seam: clears the memoized key after tests change env. */
export function resetTopupSettingsKeyCache() {
  cachedKey = null;
}
