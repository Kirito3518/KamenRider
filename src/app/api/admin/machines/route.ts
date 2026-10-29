import { machineApi } from "@/lib/machine-api";
export function GET(r: Request) {
  return machineApi(r, true, (_, call) => call("/internal/machines"));
}
