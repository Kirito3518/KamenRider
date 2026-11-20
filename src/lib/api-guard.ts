/**
 * Shared session/same-origin guard for financial route handlers.
 *
 * Kept separate from machine-api.ts so top-up routes never touch the
 * Controller client and the existing machine/rental flows stay unchanged.
 */

import { getAuth } from "@/lib/auth";

export type ApiActor = {
  id: string;
  role: string;
  name: string;
  email: string;
};

export class ApiError extends Error {
  readonly status: number;
  readonly code: string;

  constructor(status: number, code: string, message: string) {
    super(message);
    this.name = "ApiError";
    this.status = status;
    this.code = code;
  }
}

export type GuardOptions = {
  admin?: boolean;
  /** Allows GET-only public handlers (unused by top-up; kept for symmetry). */
  publicAccess?: boolean;
};

export const NO_STORE_HEADERS = { "Cache-Control": "no-store" } as const;

export function noStoreJson(data: unknown, status = 200, headers?: HeadersInit) {
  return Response.json(data, {
    status,
    headers: { ...NO_STORE_HEADERS, ...(headers ?? {}) },
  });
}

function expectedOrigin(request: Request) {
  return new URL(process.env.BETTER_AUTH_URL || request.url).origin;
}

export async function guardApi(
  request: Request,
  options: GuardOptions = {},
): Promise<ApiActor> {
  const session = options.publicAccess
    ? null
    : await (await getAuth()).api.getSession({ headers: request.headers });

  if (!options.publicAccess && !session) {
    throw new ApiError(401, "unauthenticated", "กรุณาเข้าสู่ระบบ");
  }
  if (options.admin && session?.user.role !== "admin") {
    throw new ApiError(403, "forbidden", "ไม่มีสิทธิ์เข้าถึงข้อมูลผู้ดูแลระบบ");
  }
  if (
    request.method !== "GET" &&
    request.headers.get("origin") !== expectedOrigin(request)
  ) {
    throw new ApiError(403, "forbidden-origin", "คำขอมาจากเว็บไซต์ที่ไม่ได้รับอนุญาต");
  }

  return {
    id: session?.user.id ?? "",
    role: session?.user.role ?? "user",
    name: session?.user.name ?? "",
    email: session?.user.email ?? "",
  };
}

export function apiJsonError(
  error: unknown,
  map?: (error: unknown) => Response | null,
): Response {
  const mapped = map?.(error);
  if (mapped) return mapped;

  if (error instanceof ApiError) {
    return noStoreJson({ error: error.message, code: error.code }, error.status);
  }
  if (error instanceof SyntaxError || error instanceof TypeError) {
    return noStoreJson(
      { error: "ข้อมูลคำขอไม่ถูกต้อง", code: "invalid-request" },
      400,
    );
  }

  console.error(
    "API request failed",
    error instanceof Error ? error.name : "unknown",
  );
  return noStoreJson(
    { error: "ดำเนินการไม่สำเร็จ กรุณาลองใหม่", code: "internal-error" },
    500,
  );
}

/** Reads a JSON object body with a size guard; throws TypeError for bad input. */
export async function readJsonObject(
  request: Request,
  maxBytes = 64 * 1024,
): Promise<Record<string, unknown>> {
  const lengthHeader = request.headers.get("content-length");
  if (lengthHeader && Number(lengthHeader) > maxBytes) {
    throw new ApiError(413, "request-too-large", "ข้อมูลคำขอมีขนาดใหญ่เกินกำหนด");
  }
  const text = await request.text();
  if (text.length > maxBytes) {
    throw new ApiError(413, "request-too-large", "ข้อมูลคำขอมีขนาดใหญ่เกินกำหนด");
  }
  let body: unknown;
  try {
    body = JSON.parse(text);
  } catch {
    throw new TypeError("Invalid JSON");
  }
  if (!body || typeof body !== "object" || Array.isArray(body)) {
    throw new TypeError("Invalid request body");
  }
  return body as Record<string, unknown>;
}

export function rejectUnexpectedFields(
  body: Record<string, unknown>,
  allowed: readonly string[],
) {
  const allowedSet = new Set(allowed);
  for (const key of Object.keys(body)) {
    if (!allowedSet.has(key)) throw new TypeError("Unexpected request field");
  }
}
