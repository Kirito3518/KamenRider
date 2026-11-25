import { describe, expect, it } from "vitest";
import type { Machine } from "@/lib/machine-contracts";
import {
  machineAvailabilityBucket,
  machineAvailabilitySegments,
  summarizeMachines,
} from "@/lib/machine-summary";

const NOW = Date.parse("2026-11-22T10:00:00.000Z");
const FRESH = new Date(NOW - 5_000).toISOString();
const STALE = new Date(NOW - 120_000).toISOString();

const baseMachine: Omit<Machine, "machineId"> = {
  approvalStatus: "approved",
  displayName: "PC",
  hostname: null,
  cpu: null,
  gpu: null,
  ramBytes: 0,
  appliedSunshineCredentialRevision: 0,
  localIp: "192.168.1.10",
  vncLocalIp: null,
  hourlyPriceMinorUnits: 100,
  moonlightPort: 47989,
  sunshinePort: 47990,
  configRevision: 1,
  appliedConfigRevision: 1,
  readiness: "notReady",
  canRent: false,
  allocationHeld: false,
  lastSeenAt: STALE,
  agentVersion: "1.0.0",
  activeAdminVncSessions: 0,
  readinessDiagnostics: null,
};

function machine(overrides: Partial<Machine> & Pick<Machine, "machineId">): Machine {
  return { ...baseMachine, ...overrides } as Machine;
}

function fixtureMachines(): Machine[] {
  return [
    // Pending machines are counted separately and never part of availability.
    machine({ machineId: "pending", approvalStatus: "pending", canRent: true, lastSeenAt: FRESH }),
    // Online and explicitly rentable.
    machine({
      machineId: "rentable-online",
      canRent: true,
      operationalState: "available",
      readiness: "ready",
      lastSeenAt: FRESH,
    }),
    // Offline but wake-ready: the Controller still reports it as rentable.
    machine({
      machineId: "rentable-offline",
      canRent: true,
      operationalState: "available",
      wakeReady: true,
      readiness: "notReady",
      lastSeenAt: STALE,
    }),
    // Leased to a renter.
    machine({
      machineId: "busy",
      canRent: false,
      operationalState: "busy",
      readiness: "notReady",
      lastSeenAt: FRESH,
    }),
    // Post-lease clearing folds into the preparing bucket.
    machine({
      machineId: "clearing",
      canRent: false,
      operationalState: "clearing",
      readiness: "ready",
      lastSeenAt: FRESH,
    }),
    machine({
      machineId: "maintenance",
      canRent: false,
      availability: "maintenance",
      operationalState: "maintenance",
      readiness: "unreachable",
      lastSeenAt: FRESH,
    }),
    machine({
      machineId: "disabled",
      canRent: false,
      availability: "disabled",
      operationalState: "disabled",
      readiness: "unknown",
      lastSeenAt: FRESH,
    }),
    machine({
      machineId: "online-idle",
      canRent: false,
      operationalState: "onlineIdle",
      readiness: "ready",
      lastSeenAt: FRESH,
    }),
    machine({
      machineId: "unknown-state",
      canRent: false,
      operationalState: "someFancyState",
      readiness: "weird",
      lastSeenAt: FRESH,
    }),
  ];
}

describe("summarizeMachines", () => {
  it("classifies every approved machine exactly once and keeps readiness counts", () => {
    const summary = summarizeMachines(fixtureMachines(), NOW);
    expect(summary).toMatchObject({
      total: 9,
      pendingApproval: 1,
      approved: 8,
      online: 7,
      offline: 1,
      rentable: 2,
      busy: 1,
      preparing: 1,
      idle: 1,
      maintenance: 1,
      disabled: 1,
      unknownState: 1,
      wakeReadyOffline: 1,
      readiness: { ready: 3, notReady: 2, unreachable: 1, unknown: 2 },
    });
    // Segments (the availability denominator) exclude pending approvals and
    // always sum to the approved machine count.
    const segments = machineAvailabilitySegments(summary);
    expect(segments.reduce((total, segment) => total + segment.value, 0)).toBe(summary.approved);
    expect(
      Object.fromEntries(segments.map((segment) => [segment.key, segment.value])),
    ).toEqual({
      rentable: 2,
      busy: 1,
      preparing: 1,
      idle: 1,
      maintenance: 1,
      disabled: 1,
      unknownState: 1,
    });
  });

  it("keeps an offline wake-ready machine rentable while reporting it offline", () => {
    const summary = summarizeMachines(
      [
        machine({
          machineId: "wake-ready",
          canRent: true,
          operationalState: "available",
          wakeReady: true,
          lastSeenAt: STALE,
        }),
      ],
      NOW,
    );
    expect(summary.rentable).toBe(1);
    expect(summary.online).toBe(0);
    expect(summary.offline).toBe(1);
    expect(summary.wakeReadyOffline).toBe(1);
  });

  it("derives the bucket from approval, availability and Controller state", () => {
    expect(machineAvailabilityBucket(machine({ machineId: "a", canRent: true, operationalState: "available" }))).toBe("rentable");
    expect(machineAvailabilityBucket(machine({ machineId: "b", operationalState: "clearing" }))).toBe("preparing");
    expect(machineAvailabilityBucket(machine({ machineId: "c", operationalState: "onlineIdle" }))).toBe("idle");
    expect(machineAvailabilityBucket(machine({ machineId: "d", approvalStatus: "pending" }))).toBe("pendingApproval");
    expect(
      machineAvailabilityBucket(
        machine({ machineId: "e", canRent: true, availability: "maintenance" }),
      ),
    ).toBe("maintenance");
    expect(
      machineAvailabilityBucket(
        machine({ machineId: "f", canRent: true, availability: "disabled" }),
      ),
    ).toBe("disabled");
    expect(machineAvailabilityBucket(machine({ machineId: "g", canRent: false }))).toBe("unknownState");
  });

  it("returns an empty breakdown for an empty fleet", () => {
    const summary = summarizeMachines([], NOW);
    expect(summary.total).toBe(0);
    expect(summary.approved).toBe(0);
    expect(machineAvailabilitySegments(summary).every((segment) => segment.value === 0)).toBe(true);
    expect(machineAvailabilitySegments(summary).reduce((total, segment) => total + segment.value, 0)).toBe(0);
  });

  it("counts only pending approvals when no machine is approved yet", () => {
    const summary = summarizeMachines(
      [machine({ machineId: "p1", approvalStatus: "pending" }), machine({ machineId: "p2", approvalStatus: "pending" })],
      NOW,
    );
    expect(summary.pendingApproval).toBe(2);
    expect(summary.approved).toBe(0);
    expect(summary.offline).toBe(0);
    expect(machineAvailabilitySegments(summary).reduce((total, segment) => total + segment.value, 0)).toBe(0);
  });
});
