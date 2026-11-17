import type {
  Machine,
  ReadinessDiagnosticCheckId,
  ReadinessDiagnosticStatus,
} from "@/lib/machine-contracts";

/** Tone names shared with the shadcn `Badge` variants (no raw palette colors). */
export type MachineStatusTone =
  | "available"
  | "busy"
  | "maintenance"
  | "secondary"
  | "outline";

export const READINESS_CHECK_LABELS: Record<
  ReadinessDiagnosticCheckId,
  string
> = {
  baseline: "การตรวจพื้นฐาน",
  bootChallenge: "การทดสอบยืนยันการบูต",
  restoration: "การตรวจการคืนค่าสภาพเครื่อง",
  streamFence: "การตรวจป้องกันการสตรีมก่อนพร้อม",
  desktopHealth: "การตรวจสุขภาพเดสก์ท็อป",
  configurationRevision: "การตรวจ revision การตั้งค่า",
  leaseReconciliation: "การตรวจสถานะ lease",
  runtimeHealth: "การตรวจความพร้อมขณะทำงาน",
};

export const READINESS_STATUS_LABELS: Record<
  ReadinessDiagnosticStatus,
  string
> = {
  passed: "ผ่าน",
  blocked: "ถูกบล็อก",
  unavailable: "ไม่มีระบบตรวจสอบ",
  unknown: "ยังไม่ทราบผล",
};

export function readinessStatusTone(
  status: ReadinessDiagnosticStatus,
): MachineStatusTone {
  switch (status) {
    case "passed":
      return "available";
    case "blocked":
      return "maintenance";
    case "unavailable":
      return "busy";
    default:
      return "outline";
  }
}

export function isPendingMachine(machine: Machine) {
  return machine.approvalStatus === "pending";
}

export function isApprovedMachine(machine: Machine) {
  return machine.approvalStatus === "approved";
}

export function approvalTone(status: string): MachineStatusTone {
  if (status === "pending") return "busy";
  if (status === "approved") return "available";
  return "outline";
}

export function approvalLabel(status: string) {
  if (status === "pending") return "รออนุมัติ";
  if (status === "approved") return "อนุมัติแล้ว";
  return status || "ไม่ทราบสถานะ";
}

/**
 * The Controller computes `readiness` as `ready` / `notReady` / `unreachable` /
 * `unknown` (see `EvaluateRentability`); anything else is shown verbatim.
 */
export function readinessTone(readiness: string | null | undefined) {
  switch (readiness) {
    case "ready":
      return "available" as const;
    case "notReady":
      return "busy" as const;
    case "unreachable":
      return "maintenance" as const;
    case "unknown":
    case undefined:
    case null:
    case "":
      return "outline" as const;
    default:
      return "outline" as const;
  }
}

export function readinessLabel(readiness: string | null | undefined) {
  switch (readiness) {
    case "ready":
      return "พร้อม";
    case "notReady":
      return "ยังไม่พร้อม";
    case "unreachable":
      return "ติดต่อไม่ได้";
    case "unknown":
    case undefined:
    case null:
    case "":
      return "ยังไม่ยืนยัน";
    default:
      return readiness;
  }
}

/** Badge tone for an Agent update operation status (labels live in agent-releases). */
export function agentUpdateTone(
  status: string | null | undefined,
): MachineStatusTone {
  switch (status) {
    case "failed":
      return "maintenance";
    case "awaitingInstaller":
    case "installing":
    case "awaitingHeartbeat":
    case "rollingBack":
      return "busy";
    case "completed":
      return "available";
    case "rolledBack":
      return "secondary";
    case "expired":
      return "outline";
    default:
      return "secondary";
  }
}

/** Rental readiness is a Controller decision (`canRent`); the UI never re-derives it. */
export function rentabilityTone(machine: Machine): MachineStatusTone {
  return machine.canRent ? "available" : "outline";
}

export function rentabilityLabel(machine: Machine) {
  return machine.canRent ? "พร้อมให้เช่า" : "ยังไม่พร้อม";
}
