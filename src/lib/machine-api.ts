import { getAuth } from "@/lib/auth";
import { controllerRequest, ControllerError } from "@/lib/controller-client";
export async function machineApi(
  request: Request,
  admin: boolean,
  action: (
    actor: { id: string; role: string },
    call: <T>(path: string, init?: RequestInit) => Promise<T>,
  ) => Promise<unknown>,
  publicAccess = false,
) {
  try {
    const session = publicAccess
      ? null
      : await (await getAuth()).api.getSession({ headers: request.headers });
    if (!publicAccess && !session)
      return Response.json(
        { error: "Authentication required" },
        { status: 401 },
      );
    if (admin && session?.user.role !== "admin")
      return Response.json({ error: "Forbidden" }, { status: 403 });
    if (
      request.method !== "GET" &&
      request.headers.get("origin") !==
        new URL(process.env.BETTER_AUTH_URL || request.url).origin
    )
      return Response.json({ error: "Forbidden origin" }, { status: 403 });
    const actor = {
      id: session?.user.id || "",
      role: session?.user.role || "user",
    };
    const result = await action(actor, <T>(path: string, init?: RequestInit) =>
      controllerRequest<T>(path, {
        ...init,
        headers: {
          "Content-Type": "application/json",
          "X-Authenticated-Actor-Id": actor.id,
          "X-Authenticated-Actor-Role": actor.role,
        },
      }),
    );
    return Response.json(result, { headers: { "Cache-Control": "no-store" } });
  } catch (error) {
    const status =
      error instanceof ControllerError
        ? error.status
        : error instanceof SyntaxError || error instanceof TypeError
          ? 400
          : 503;
    return Response.json(
      {
        error:
          status === 503
            ? "Controller unavailable"
            : status === 400
              ? "Invalid request"
              : "Operation rejected",
      },
      { status, headers: { "Cache-Control": "no-store" } },
    );
  }
}
export const segment = (value: string) => encodeURIComponent(value);
export function integer(
  value: unknown,
  min = 0,
  max = Number.MAX_SAFE_INTEGER,
) {
  if (
    !Number.isSafeInteger(value) ||
    Number(value) < min ||
    Number(value) > max
  )
    throw new TypeError("Invalid integer");
  return Number(value);
}
export function text(value: unknown) {
  if (typeof value !== "string" || !value.trim() || value.length > 128)
    throw new TypeError("Invalid text");
  return value;
}
