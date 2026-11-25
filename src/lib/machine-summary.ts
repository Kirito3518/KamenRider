/**
 * Pure machine-summary helpers for the admin overview dashboard.
 *
 * The Controller computes rentability (`canRent`) itself, and an offline machine
 * with `wakeReady` may still be rentable because the Controller can wake it. That
 * is why availability is exposed as `rentable` while `online` / `offline` are
 * reported separately. Pending approvals are counted on their own and are
 * excluded from the availability denominator (the donut chart segments).
 *
 * Client-safe: only type-only imports plus the pure `isAgentOnline` helper.
 */

import { isAgentOnline } from "@/lib/agent-releases";
import type { Machine } from "@/lib/machine-contracts";

export type MachineReadinessCounts = {
  ready: number;
  notReady: number;
  unreachable: number;
  unknown: number;
};

export type MachineSummary = {
  /** Every machine registered with the Controller. */
  total: number;
  /** approvalStatus === "approved"; the availability denominator. */
  approved: number;
  /** approvalStatus === "pending"; excluded from availability segments. */
  pendingApproval: number;
  /** Approved machines whose Agent heartbeat is still fresh. */
  online: number;
  /** Approved machines without a fresh Agent heartbeat. */
  offline: number;
  /** Approved machines the Controller reports as rentable (may be offline + wake-ready). */
  rentable: number;
  /** Approved machines currently leased to a renter. */
  busy: number;
  /** Approved machines preparing/booting/clearing after a lease. */
  preparing: number;
  /** Approved machines online but not rentable (e.g. `onlineIdle`). */
  idle: number;
  /** Admin set the machine to maintenance (or the Controller reports maintenance). */
  maintenance: number;
  /** Admin disabled renting on the machine. */
  disabled: number;
  /** Approved machines whose Controller state is not recognized. */
  unknownState: number;
  /** Approved offline machines the Controller can wake on demand. */
  wakeReadyOffline: number;
  /** Agent readiness diagnostics, counted over approved machines. */
  readiness: MachineReadinessCounts;
};

export type MachineAvailabilitySegmentKey =
  | "rentable"
  | "busy"
  | "preparing"
  | "idle"
  | "maintenance"
  | "disabled"
  | "unknownState";

export type MachineAvailabilitySegment = {
  key: MachineAvailabilitySegmentKey;
  label: string;
  value: number;
  /** Semantic design token, never a raw colour. */
  color: string;
};

/** Segment metadata is exported so charts and tests share one vocabulary. */
export const MACHINE_AVAILABILITY_SEGMENT_META: Record<
  MachineAvailabilitySegmentKey,
  { label: string; color: string }
> = {
  rentable: { label: "พร้อมให้เช่า", color: "var(--status-available)" },
  busy: { label: "กำลังใช้งาน", color: "var(--status-busy)" },
  preparing: { label: "กำลังเตรียม/เคลียร์", color: "var(--chart-3)" },
  idle: { label: "ออนไลน์แต่ไม่ว่าง", color: "var(--chart-4)" },
  maintenance: { label: "ซ่อมบำรุง", color: "var(--status-maintenance)" },
  disabled: { label: "ปิดให้เช่า", color: "var(--chart-5)" },
  unknownState: { label: "ไม่ทราบสถานะ", color: "var(--muted-foreground)" },
};

/** Operational states that mean "the machine is busy preparing for a renter". */
const PREPARING_STATES = new Set([
  "preparing",
  "booting",
  "awaitingPower",
  "ending",
  "clearing",
]);

export type MachineAvailabilityBucket = MachineAvailabilitySegmentKey | "pendingApproval";

/**
 * Classifies one machine. Controller fields win over the admin `availability`
 * setting for maintenance/disabled because either source disables renting.
 */
export function machineAvailabilityBucket(
  machine: Pick<
    Machine,
    "approvalStatus" | "availability" | "operationalState" | "canRent"
  >,
): MachineAvailabilityBucket {
  if (machine.approvalStatus === "pending") return "pendingApproval";
  if (machine.approvalStatus !== "approved") return "unknownState";
  if (
    machine.availability === "maintenance" ||
    machine.operationalState === "maintenance"
  ) {
    return "maintenance";
  }
  if (
    machine.availability === "disabled" ||
    machine.operationalState === "disabled"
  ) {
    return "disabled";
  }
  const state = machine.operationalState;
  if (state === "busy") return "busy";
  if (state && PREPARING_STATES.has(state)) return "preparing";
  if (state === "onlineIdle") return "idle";
  if (machine.canRent) return "rentable";
  return "unknownState";
}

function readinessBucket(readiness: string | null | undefined): keyof MachineReadinessCounts {
  switch (readiness) {
    case "ready":
      return "ready";
    case "notReady":
      return "notReady";
    case "unreachable":
      return "unreachable";
    default:
      return "unknown";
  }
}

/**
 * Summarizes Controller machines for the overview cards and donut chart.
 * `pendingApproval` machines are counted but not part of the availability
 * segments, so segment values always sum to `approved`.
 */
export function summarizeMachines(machines: Machine[], now = Date.now()): MachineSummary {
  const summary: MachineSummary = {
    total: machines.length,
    approved: 0,
    pendingApproval: 0,
    online: 0,
    offline: 0,
    rentable: 0,
    busy: 0,
    preparing: 0,
    idle: 0,
    maintenance: 0,
    disabled: 0,
    unknownState: 0,
    wakeReadyOffline: 0,
    readiness: { ready: 0, notReady: 0, unreachable: 0, unknown: 0 },
  };
  for (const machine of machines) {
    if (machine.approvalStatus === "pending") {
      summary.pendingApproval += 1;
      continue;
    }
    if (machine.approvalStatus !== "approved") continue;
    summary.approved += 1;
    const online = isAgentOnline(machine, now);
    if (online) summary.online += 1;
    else {
      summary.offline += 1;
      if (machine.wakeReady) summary.wakeReadyOffline += 1;
    }
    const bucket = machineAvailabilityBucket(machine);
    if (bucket !== "pendingApproval") summary[bucket] += 1;
    summary.readiness[readinessBucket(machine.readiness)] += 1;
  }
  return summary;
}

/**
 * Donut-chart segments for the availability breakdown. Values sum to
 * `summary.approved`; machines waiting for approval are reported separately.
 */
export function machineAvailabilitySegments(
  summary: Pick<MachineSummary, MachineAvailabilitySegmentKey>,
): MachineAvailabilitySegment[] {
  return (Object.keys(MACHINE_AVAILABILITY_SEGMENT_META) as MachineAvailabilitySegmentKey[]).map(
    (key) => ({
      key,
      label: MACHINE_AVAILABILITY_SEGMENT_META[key].label,
      value: summary[key],
      color: MACHINE_AVAILABILITY_SEGMENT_META[key].color,
    }),
  );
}
