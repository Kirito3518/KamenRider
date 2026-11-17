import { machineApi, text } from "@/lib/machine-api";
import type { SunshineCredentialState } from "@/lib/machine-contracts";

const MAX_PASSWORD_LENGTH = 256;

/**
 * The Controller never returns the Sunshine password, but the web route still
 * rebuilds a known-shape state object so a future Controller regression can
 * never leak a password to the Admin browser.
 */
function credentialState(value: unknown): SunshineCredentialState {
  if (!value || typeof value !== "object" || Array.isArray(value))
    throw new Error("Controller returned an invalid Sunshine credential state");
  const record = value as Record<string, unknown>;
  const revision =
    typeof record.revision === "number" &&
    Number.isSafeInteger(record.revision) &&
    record.revision >= 0
      ? record.revision
      : null;
  if (revision === null)
    throw new Error("Controller returned an invalid Sunshine credential state");
  return {
    configured: record.configured === true,
    userName:
      typeof record.userName === "string" && record.userName.trim()
        ? record.userName
        : null,
    revision,
    updatedAtUtc:
      typeof record.updatedAtUtc === "string" && record.updatedAtUtc
        ? record.updatedAtUtc
        : null,
  };
}

function credentialRequest(value: unknown) {
  if (!value || typeof value !== "object" || Array.isArray(value))
    throw new TypeError("Invalid request");
  const record = value as Record<string, unknown>;
  if (Object.keys(record).some((key) => key !== "userName" && key !== "password"))
    throw new TypeError("Unexpected request field");
  const userName = text(record.userName).trim();
  if (/[\u0000-\u001f\u007f]/.test(userName))
    throw new TypeError("Invalid user name");
  const password = record.password;
  if (
    typeof password !== "string" ||
    password.length === 0 ||
    password.length > MAX_PASSWORD_LENGTH
  )
    throw new TypeError("Invalid password");
  return { userName, password };
}

export function GET(request: Request) {
  return machineApi(request, true, async (_actor, call) =>
    credentialState(await call<unknown>("/internal/sunshine-credential")),
  );
}

export function PUT(request: Request) {
  return machineApi(request, true, async (_actor, call) => {
    // Read inside the authenticated, same-origin action: the password only ever
    // exists in this request body and the Controller PUT, never in logs/state.
    let body: unknown;
    try {
      body = await request.json();
    } catch {
      throw new TypeError("Invalid request");
    }
    const credential = credentialRequest(body);
    return credentialState(
      await call<unknown>("/internal/sunshine-credential", {
        method: "PUT",
        body: JSON.stringify(credential),
      }),
    );
  });
}
