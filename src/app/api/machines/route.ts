import { machineApi } from "@/lib/machine-api";
import type { Machine } from "@/lib/machine-contracts";
export function GET(request: Request) {
  return machineApi(
    request,
    false,
    async (_, call) =>
      (await call<Machine[]>("/internal/machines"))
        .filter((m) => m.approvalStatus === "approved" && m.canRent === true)
        .map((m) => ({
          id: m.machineId,
          name: m.displayName,
          price: m.hourlyPriceMinorUnits / 100,
          status: m.canRent === true ? "available" : m.readiness === "ready" ? "busy" : m.readiness,
          cpu: "โ€”",
          gpu: "โ€”",
          ram: 0,
          storage: "โ€”",
          game: "",
        })),
    true,
  );
}
