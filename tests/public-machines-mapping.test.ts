import { describe, expect, it } from "vitest";
import type { Machine } from "@/lib/machine-contracts";
import { rentalStatus, toPublicMachines } from "@/lib/public-machines";

function controllerMachine(overrides: Record<string, unknown> = {}): Machine {
  return {
    machineId: "pc-1",
    approvalStatus: "approved",
    displayName: "DESKTOP-ONE",
    hostname: "DESKTOP-ONE",
    cpu: "AMD Ryzen 7 7800X3D",
    gpu: "NVIDIA GeForce RTX 4080",
    ramBytes: 34_359_738_368,
    appliedSunshineCredentialRevision: 3,
    hourlyPriceMinorUnits: 4_000,
    localIp: "192.168.1.10",
    vncLocalIp: "192.168.1.11",
    moonlightPort: 47989,
    sunshinePort: 47990,
    configRevision: 2,
    appliedConfigRevision: 2,
    readiness: "ready",
    canRent: true,
    publicKeyFingerprint: "secret-management-fingerprint",
    lastSeenAt: "2026-11-17T06:00:00.000Z",
    agentVersion: "1.0.0",
    activeAdminVncSessions: 0,
    ...overrides,
  } as Machine;
}

describe("toPublicMachines", () => {
  it("exposes specs for approved rentable machines and nothing private", () => {
    const machines = toPublicMachines([
      controllerMachine(),
      controllerMachine({ machineId: "pending-pc", approvalStatus: "pending" }),
      controllerMachine({ machineId: "held-pc", canRent: false }),
    ]);

    expect(machines).toEqual([
      {
        id: "pc-1",
        name: "DESKTOP-ONE",
        priceSatang: 4_000,
        status: "available",
        cpu: "AMD Ryzen 7 7800X3D",
        gpu: "NVIDIA GeForce RTX 4080",
        ram: 32,
        ramBytes: 34_359_738_368,
        storage: "—",
        game: "",
      },
    ]);

    const raw = JSON.stringify(machines);
    for (const privateValue of [
      "192.168.1.10",
      "192.168.1.11",
      "001122334455",
      "47989",
      "47990",
      "secret-management-fingerprint",
    ])
      expect(raw).not.toContain(privateValue);
  });

  it("uses neutral placeholders when the Agent has not reported hardware", () => {
    const machines = toPublicMachines([
      controllerMachine({ cpu: null, gpu: null, ramBytes: 0, hostname: null }),
    ]);

    expect(machines[0]).toMatchObject({
      cpu: "—",
      gpu: "—",
      ram: 0,
      ramBytes: 0,
    });
  });

  it("only exposes positive whole-baht prices", () => {
    const price = (hourlyPriceMinorUnits: unknown) =>
      toPublicMachines([
        controllerMachine({ hourlyPriceMinorUnits }),
      ]).length;

    expect(price(100)).toBe(1);
    expect(price(4_000)).toBe(1);
    expect(price(99)).toBe(0);
    expect(price(4_099)).toBe(0);
    expect(price(4_000.5)).toBe(0);
    expect(price(0)).toBe(0);
    expect(price(-4_000)).toBe(0);
    expect(price("4000")).toBe(0);
    expect(price(Number.NaN)).toBe(0);
  });

  it("respects the admin availability mode, defaulting to rentable", () => {
    const ids = (machines: Machine[]) =>
      toPublicMachines(machines).map((machine) => machine.id);

    expect(ids([controllerMachine({ availability: undefined })])).toEqual(["pc-1"]);
    expect(ids([controllerMachine({ availability: "rentable" })])).toEqual(["pc-1"]);
    expect(ids([controllerMachine({ availability: "maintenance" })])).toEqual([]);
    expect(ids([controllerMachine({ availability: "disabled" })])).toEqual([]);
  });

  it("maps lifecycle states and hides machines with no usable state", () => {
    const status = (overrides: Record<string, unknown>) =>
      toPublicMachines([controllerMachine(overrides)])[0]?.status;

    expect(status({ operationalState: "available", canRent: true })).toBe("available");
    expect(status({ operationalState: "available", canRent: false })).toBe("notReady");
    expect(status({ operationalState: "busy" })).toBe("busy");
    expect(status({ operationalState: "maintenance" })).toBe("maintenance");
    expect(status({ operationalState: undefined, canRent: true })).toBe("available");
    expect(status({ operationalState: undefined, canRent: false })).toBeUndefined();
    expect(status({ operationalState: "something-new", canRent: true })).toBe("available");
    expect(status({ operationalState: "something-new", canRent: false })).toBe("notReady");
  });
});

describe("rentalStatus", () => {
  it("passes known lifecycle states through and defaults to rentability", () => {
    expect(rentalStatus("busy", true)).toBe("busy");
    expect(rentalStatus("clearing", true)).toBe("clearing");
    expect(rentalStatus("disabled", true)).toBe("disabled");
    expect(rentalStatus("available", true)).toBe("available");
    expect(rentalStatus("available", false)).toBe("notReady");
    expect(rentalStatus(undefined, true)).toBe("available");
    expect(rentalStatus(undefined, false)).toBe("notReady");
    expect(rentalStatus("future-state", true)).toBe("available");
    expect(rentalStatus("future-state", false)).toBe("notReady");
  });
});
