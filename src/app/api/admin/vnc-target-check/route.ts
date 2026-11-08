import { machineApi, text } from "@/lib/machine-api";

type TargetProbeResponse = {
  supported: boolean;
};

export function POST(request: Request) {
  return machineApi(request, true, async (_actor, call) => {
    const body = await request.json();
    if (!body || typeof body !== "object" || Array.isArray(body)) {
      throw new TypeError("Invalid VNC target probe request");
    }

    const result = await call<TargetProbeResponse>(
      "/internal/admin-vnc-target-probes",
      {
        method: "POST",
        body: JSON.stringify({
          machineId: text(body.machineId),
          targetAddress: text(body.targetAddress),
        }),
      },
    );
    if (!result || typeof result.supported !== "boolean") {
      throw new Error("Controller returned an invalid VNC target probe result");
    }
    return { supported: result.supported };
  });
}
