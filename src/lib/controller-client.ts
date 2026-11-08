import "server-only";
export class ControllerError extends Error {
  constructor(public status: number) {
    super("Controller unavailable");
  }
}
export async function controllerRequest<T>(
  path: string,
  init?: RequestInit,
): Promise<T> {
  const base = process.env.CONTROLLER_INTERNAL_URL;
  const credential = process.env.CONTROLLER_SERVICE_CREDENTIAL;
  if (!base || !credential) throw new ControllerError(503);
  const url = new URL(base);
  if (
    !["127.0.0.1", "localhost", "[::1]"].includes(url.hostname) ||
    !["http:", "https:"].includes(url.protocol) ||
    !path.startsWith("/internal/")
  )
    throw new ControllerError(503);
  try {
    const response = await fetch(new URL(path, url), {
      ...init,
      cache: "no-store",
      redirect: "error",
      signal: AbortSignal.timeout(path === "/internal/agent-releases" && init?.method === "POST" ? 300_000 : 5000),
      headers: {
        ...Object.fromEntries(new Headers(init?.headers)),
        "X-Service-Credential": credential,
      },
    });
    if (!response.ok)
      throw new ControllerError(
        [400, 403, 404, 409, 413, 429].includes(response.status)
          ? response.status
          : 503,
      );
    return (await response.json()) as T;
  } catch (error) {
    if (error instanceof ControllerError) throw error;
    throw new ControllerError(503);
  }
}
