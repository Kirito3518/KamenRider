/**
 * Browser-side sealing for the renter's Parsec credentials.
 *
 * The Agent advertises a boot-only RSA public key. We encrypt a random AES-256-GCM key
 * with RSA-OAEP(SHA-256), then encrypt the JSON credentials with that AES key. The
 * Controller only relays ciphertext; only the Agent on the rented machine can open it,
 * and the key never leaves memory there.
 *
 * Base64url without padding matches the Agent/Controller wire format.
 */

export type ParsecSealedSecret = {
  wrappedKey: string;
  iv: string;
  ciphertext: string;
};

export type ParsecCredentials = {
  email: string;
  password: string;
};

function toBase64Url(bytes: Uint8Array): string {
  let binary = "";
  for (const byte of bytes) binary += String.fromCharCode(byte);
  return btoa(binary).replace(/\+/g, "-").replace(/\//g, "_").replace(/=+$/g, "");
}

function fromBase64Url(value: string): Uint8Array {
  const padded = value.replace(/-/g, "+").replace(/_/g, "/");
  const binary = atob(padded + "=".repeat((4 - (padded.length % 4)) % 4));
  const bytes = new Uint8Array(binary.length);
  for (let index = 0; index < binary.length; index += 1) {
    bytes[index] = binary.charCodeAt(index);
  }
  return bytes;
}

export async function sealParsecCredentials(
  publicKeySpkiBase64Url: string,
  credentials: ParsecCredentials,
  cryptoImpl: Crypto = crypto,
): Promise<ParsecSealedSecret> {
  if (!publicKeySpkiBase64Url) {
    throw new Error("Parsec public key is unavailable");
  }
  if (!credentials.email.trim() || !credentials.password) {
    throw new Error("Parsec email and password are required");
  }

  const publicKey = await cryptoImpl.subtle.importKey(
    "spki",
    fromBase64Url(publicKeySpkiBase64Url) as BufferSource,
    { name: "RSA-OAEP", hash: "SHA-256" },
    false,
    ["encrypt"],
  );

  const aesKey = await cryptoImpl.subtle.generateKey(
    { name: "AES-GCM", length: 256 },
    true,
    ["encrypt"],
  );
  const iv = cryptoImpl.getRandomValues(new Uint8Array(12));
  const plaintext = new TextEncoder().encode(
    JSON.stringify({
      email: credentials.email.trim(),
      password: credentials.password,
    }),
  );
  const ciphertext = new Uint8Array(
    await cryptoImpl.subtle.encrypt(
      { name: "AES-GCM", iv: iv as BufferSource },
      aesKey,
      plaintext as BufferSource,
    ),
  );
  const rawAesKey = new Uint8Array(
    await cryptoImpl.subtle.exportKey("raw", aesKey),
  );
  const wrappedKey = new Uint8Array(
    await cryptoImpl.subtle.encrypt(
      { name: "RSA-OAEP" },
      publicKey,
      rawAesKey as BufferSource,
    ),
  );

  plaintext.fill(0);
  rawAesKey.fill(0);

  return {
    wrappedKey: toBase64Url(wrappedKey),
    iv: toBase64Url(iv),
    ciphertext: toBase64Url(ciphertext),
  };
}
