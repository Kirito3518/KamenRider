import { machineApi, segment, text } from "@/lib/machine-api";

/**
 * Sealed secret fields are browser-produced base64url strings, so they cannot use the generic
 * `text()` bound (128 characters): an RSA-2048 OAEP wrapped key alone is 342 base64url
 * characters. These bounds mirror the Controller's `ParsecState.ValidBase64Url` limits
 * (wrappedKey <= 512, iv <= 64, ciphertext <= 8192) so a real sealed secret reaches the
 * Controller instead of failing validation in the proxy.
 */
const base64Url = /^[A-Za-z0-9_-]+$/;

function boundedBase64Url(value: unknown, maxLength: number) {
  if (
    typeof value !== "string" ||
    value.length > maxLength ||
    !base64Url.test(value)
  )
    throw new TypeError("Invalid sealed secret");
  return value;
}

function requestObject(value: unknown) {
  if (!value || typeof value !== "object" || Array.isArray(value))
    throw new TypeError("Invalid request");
  return value as Record<string, unknown>;
}

function sealedSecret(value: unknown) {
  if (!value || typeof value !== "object" || Array.isArray(value))
    throw new TypeError("Invalid sealed secret");
  const record = value as Record<string, unknown>;
  return {
    wrappedKey: boundedBase64Url(record.wrappedKey, 512),
    iv: boundedBase64Url(record.iv, 64),
    ciphertext: boundedBase64Url(record.ciphertext, 8192),
  };
}

export async function GET(
  r: Request,
  { params }: { params: Promise<{ rentalId: string }> },
) {
  const { rentalId } = await params;
  return machineApi(r, false, (_, call) =>
    call(`/internal/leases/${segment(rentalId)}/parsec`),
  );
}

export async function POST(
  r: Request,
  { params }: { params: Promise<{ rentalId: string }> },
) {
  const { rentalId } = await params;
  return machineApi(r, false, async (actor, call) => {
    const body = requestObject(await r.json());
    const idempotencyKey = text(body.idempotencyKey);
    if (idempotencyKey.length > 128) throw new TypeError("Invalid key");
    // Optional round-two marker for Parsec's e-mail/IP challenge. Treat null like absent;
    // reject every other value that is not a bounded non-empty string.
    const retryAttemptId =
      body.retryAttemptId === undefined || body.retryAttemptId === null
        ? undefined
        : text(body.retryAttemptId);
    return call(`/internal/leases/${segment(rentalId)}/parsec`, {
      method: "POST",
      body: JSON.stringify({
        userId: actor.id,
        idempotencyKey,
        ...(retryAttemptId === undefined ? {} : { retryAttemptId }),
        secret: sealedSecret(body.secret),
      }),
    });
  });
}
