import { describe, expect, it } from "vitest";
import { createDecipheriv, createPrivateKey, privateDecrypt, constants } from "node:crypto";
import { webcrypto } from "node:crypto";
import { sealParsecCredentials } from "@/lib/parsec-crypto";

function base64Url(bytes: Uint8Array) {
  return Buffer.from(bytes)
    .toString("base64")
    .replace(/\+/g, "-")
    .replace(/\//g, "_")
    .replace(/=+$/g, "");
}

function fromBase64Url(value: string) {
  const padded = value.replace(/-/g, "+").replace(/_/g, "/");
  return Buffer.from(padded + "=".repeat((4 - (padded.length % 4)) % 4), "base64");
}

async function makeKeyPair() {
  const pair = (await webcrypto.subtle.generateKey(
    {
      name: "RSA-OAEP",
      modulusLength: 2048,
      publicExponent: new Uint8Array([1, 0, 1]),
      hash: "SHA-256",
    },
    true,
    ["encrypt", "decrypt"],
  )) as CryptoKeyPair;
  return pair;
}

/**
 * Mirrors the Agent's decryption: RSA-OAEP(SHA-256) unwraps a 32-byte AES key and
 * AES-256-GCM opens the JSON credentials. If this test passes, the browser wire format
 * matches what the C# agent expects.
 */
describe("Parsec browser sealing", () => {
  it("round-trips credentials with RSA-OAEP + AES-256-GCM", async () => {
    const pair = await makeKeyPair();
    const spki = new Uint8Array(await webcrypto.subtle.exportKey("spki", pair.publicKey));
    const sealed = await sealParsecCredentials(
      base64Url(spki),
      { email: "renter@example.com", password: "correct horse battery staple" },
      webcrypto as unknown as Crypto,
    );

    const wrappedKey = fromBase64Url(sealed.wrappedKey);
    const iv = fromBase64Url(sealed.iv);
    const ciphertext = fromBase64Url(sealed.ciphertext);
    const privateKey = createPrivateKey({
      key: Buffer.from(
        new Uint8Array(await webcrypto.subtle.exportKey("pkcs8", pair.privateKey)),
      ),
      format: "der",
      type: "pkcs8",
    });
    const aesKey = privateDecrypt(
      {
        key: privateKey,
        padding: constants.RSA_PKCS1_OAEP_PADDING,
        oaepHash: "sha256",
      },
      wrappedKey,
    );
    expect(aesKey).toHaveLength(32);

    const authTag = ciphertext.subarray(ciphertext.length - 16);
    const body = ciphertext.subarray(0, ciphertext.length - 16);
    const decipher = createDecipheriv("aes-256-gcm", aesKey, iv);
    decipher.setAuthTag(authTag);
    const plaintext = Buffer.concat([decipher.update(body), decipher.final()]).toString("utf8");
    expect(JSON.parse(plaintext)).toEqual({
      email: "renter@example.com",
      password: "correct horse battery staple",
    });
  });

  it("refuses a missing key or empty credentials", async () => {
    await expect(
      sealParsecCredentials("", { email: "a@b.c", password: "x" }),
    ).rejects.toThrow("Parsec public key is unavailable");
    const pair = await makeKeyPair();
    const spki = base64Url(
      new Uint8Array(await webcrypto.subtle.exportKey("spki", pair.publicKey)),
    );
    await expect(
      sealParsecCredentials(spki, { email: "  ", password: "x" }, webcrypto as unknown as Crypto),
    ).rejects.toThrow("Parsec email and password are required");
  });
});
