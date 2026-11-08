import { machineApi } from "@/lib/machine-api";

export function GET(request: Request) {
  return machineApi(request, true, (_actor, call) => call("/internal/agent-releases"));
}

export function POST(request: Request) {
  return machineApi(request, true, async (_actor, call) => {
    const contentType = request.headers.get("content-type");
    if (!contentType?.toLowerCase().startsWith("multipart/form-data;"))
      throw new TypeError("Expected multipart/form-data");
    if (!request.body) throw new TypeError("Expected multipart request body");

    const init = {
      method: "POST",
      headers: { "Content-Type": contentType },
      body: request.body,
      duplex: "half",
    } as RequestInit;
    return call("/internal/agent-releases", init);
  });
}
