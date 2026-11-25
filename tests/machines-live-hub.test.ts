// @vitest-environment node

import { describe, expect, it, vi } from "vitest";

vi.mock("server-only", () => ({}));

import type { Machine } from "@/lib/machine-contracts";
import {
  createMachinesLiveHub,
  getMachinesLiveHub,
  type MachinesLiveEvent,
} from "@/lib/machines-live";

const BASE_TIME = Date.parse("2026-11-22T12:00:00.000Z");
/** Push heartbeats out of the way for tests that only drive the poll loop. */
const NO_HEARTBEAT_MS = 24 * 60 * 60 * 1_000;

function rawMachine(overrides: Record<string, unknown> = {}): Machine {
  return {
    machineId: "pc-1",
    approvalStatus: "approved",
    displayName: "PC ONE",
    hostname: "PC-ONE",
    cpu: "AMD Ryzen 7",
    gpu: "NVIDIA RTX 4070",
    ramBytes: 34_359_738_368,
    appliedSunshineCredentialRevision: 1,
    hourlyPriceMinorUnits: 4_000,
    localIp: "192.168.1.10",
    vncLocalIp: null,
    moonlightPort: 47989,
    sunshinePort: 47990,
    configRevision: 1,
    appliedConfigRevision: 1,
    readiness: "ready",
    canRent: true,
    lastSeenAt: null,
    agentVersion: "1.0.0",
    activeAdminVncSessions: 0,
    ...overrides,
  } as Machine;
}

/** Deterministic clock + timer queue so polling/backoff can be driven by hand. */
function createManualClock(start = BASE_TIME) {
  let now = start;
  let nextId = 1;
  const timers = new Map<number, { at: number; callback: () => void }>();

  return {
    now: () => now,
    setTimer: (callback: () => void, delayMs: number) => {
      const id = nextId++;
      timers.set(id, { at: now + delayMs, callback });
      return id;
    },
    clearTimer: (timer: number | ReturnType<typeof setTimeout>) => {
      timers.delete(timer as number);
    },
    advance(ms: number) {
      now += ms;
      for (;;) {
        const due = [...timers.entries()]
          .filter(([, timer]) => timer.at <= now)
          .sort((a, b) => a[1].at - b[1].at)[0];
        if (!due) break;
        timers.delete(due[0]);
        due[1].callback();
      }
    },
    pendingDelays() {
      return [...timers.values()]
        .map((timer) => timer.at - now)
        .sort((a, b) => a - b);
    },
  };
}

/** Poll/idle delays only: the 24h heartbeat timer is not interesting here. */
function pollDelays(clock: ReturnType<typeof createManualClock>) {
  return clock.pendingDelays().filter((delay) => delay < 60_000);
}

async function flush() {
  // Let `await loadMachines()` continuations, emits and reschedules settle.
  for (let i = 0; i < 12; i += 1) await Promise.resolve();
}

function collect() {
  const events: MachinesLiveEvent[] = [];
  return {
    events,
    listener: (event: MachinesLiveEvent) => events.push(event),
    machinesEvents: () =>
      events.filter(
        (event): event is Extract<MachinesLiveEvent, { type: "machines" }> =>
          event.type === "machines",
      ),
    statuses: () =>
      events.filter(
        (event): event is Extract<MachinesLiveEvent, { type: "status" }> =>
          event.type === "status",
      ),
    heartbeats: () =>
      events.filter(
        (event): event is Extract<MachinesLiveEvent, { type: "heartbeat" }> =>
          event.type === "heartbeat",
      ),
  };
}

describe("machines live hub", () => {
  it("polls the Controller once for all subscribers and fans out one snapshot", async () => {
    const clock = createManualClock();
    const loadMachines = vi.fn(async () => [rawMachine()]);
    const hub = createMachinesLiveHub({ loadMachines, clock });
    const first = collect();
    const second = collect();

    const unsubscribeFirst = hub.subscribe(first.listener);
    const unsubscribeSecond = hub.subscribe(second.listener);
    await flush();

    expect(loadMachines).toHaveBeenCalledTimes(1);
    expect(first.machinesEvents()).toHaveLength(1);
    expect(second.machinesEvents()).toHaveLength(1);
    expect(hub.isLive()).toBe(true);

    const snapshot = hub.getSnapshot();
    expect(snapshot?.machines).toHaveLength(1);
    expect(snapshot?.version).toMatch(/^[0-9a-f]{32}$/);
    expect(snapshot?.serverTimeUtc).toBe(new Date(BASE_TIME).toISOString());
    expect(snapshot?.updatedAtMs).toBe(BASE_TIME);
    expect(first.statuses()).toContainEqual({
      type: "status",
      live: true,
      serverTimeUtc: new Date(BASE_TIME).toISOString(),
    });

    // The next poll returns the same public data: no duplicate `machines` frame.
    clock.advance(2_000);
    await flush();
    expect(loadMachines).toHaveBeenCalledTimes(2);
    expect(first.machinesEvents()).toHaveLength(1);

    unsubscribeFirst();
    unsubscribeSecond();
    hub.stop();
  });

  it("emits a new `machines` event only when the public snapshot version changes", async () => {
    const clock = createManualClock();
    let machines = [rawMachine()];
    const hub = createMachinesLiveHub({
      loadMachines: async () => machines,
      clock,
      heartbeatIntervalMs: NO_HEARTBEAT_MS,
    });
    const collector = collect();
    hub.subscribe(collector.listener);
    await flush();

    const firstVersion = hub.getSnapshot()?.version;
    expect(collector.machinesEvents()).toHaveLength(1);

    machines = [rawMachine({ hourlyPriceMinorUnits: 5_000 })];
    clock.advance(2_000);
    await flush();

    expect(collector.machinesEvents()).toHaveLength(2);
    expect(hub.getSnapshot()?.version).not.toBe(firstVersion);
    expect(hub.getSnapshot()?.machines[0].priceSatang).toBe(5_000);
    hub.stop();
  });

  it("keeps the last snapshot through Controller failures and backs off 1/2/4/8/15s", async () => {
    const clock = createManualClock();
    let failing = false;
    const loadMachines = vi.fn(async () => {
      if (failing) throw new Error("Controller unavailable");
      return [rawMachine()];
    });
    const hub = createMachinesLiveHub({
      loadMachines,
      clock,
      heartbeatIntervalMs: NO_HEARTBEAT_MS,
    });
    const collector = collect();
    hub.subscribe(collector.listener);
    await flush();

    const goodSnapshot = hub.getSnapshot();
    expect(goodSnapshot).not.toBeNull();

    failing = true;
    clock.advance(2_000);
    await flush();
    expect(loadMachines).toHaveBeenCalledTimes(2);
    expect(pollDelays(clock)).toEqual([1_000]);
    expect(hub.isLive()).toBe(false);
    expect(hub.getSnapshot()).toBe(goodSnapshot);
    expect(collector.statuses()).toContainEqual(
      expect.objectContaining({ type: "status", live: false }),
    );

    for (const [callNumber, delay] of [
      [3, 2_000],
      [4, 4_000],
      [5, 8_000],
      [6, 15_000],
      [7, 15_000],
    ] as const) {
      clock.advance(pollDelays(clock)[0]);
      await flush();
      expect(loadMachines).toHaveBeenCalledTimes(callNumber);
      expect(pollDelays(clock)).toEqual([delay]);
    }

    failing = false;
    clock.advance(15_000);
    await flush();
    expect(hub.isLive()).toBe(true);
    expect(collector.statuses()).toContainEqual(
      expect.objectContaining({ type: "status", live: true }),
    );
    // A successful poll returns to the normal 2s cadence.
    expect(pollDelays(clock)).toEqual([2_000]);
    hub.stop();
  });

  it("emits a heartbeat every 15s while subscribed and stops after idle", async () => {
    const clock = createManualClock();
    const hub = createMachinesLiveHub({
      loadMachines: async () => [rawMachine()],
      clock,
    });
    const collector = collect();
    const unsubscribe = hub.subscribe(collector.listener);
    await flush();
    expect(collector.heartbeats()).toHaveLength(0);

    clock.advance(15_000);
    await flush();
    expect(collector.heartbeats()).toHaveLength(1);

    clock.advance(15_000);
    await flush();
    expect(collector.heartbeats()).toHaveLength(2);

    unsubscribe();
    hub.stop();
  });

  it("stops polling and heartbeats 30s after the last subscriber leaves, then restarts", async () => {
    const clock = createManualClock();
    const loadMachines = vi.fn(async () => [rawMachine()]);
    const hub = createMachinesLiveHub({
      loadMachines,
      clock,
      heartbeatIntervalMs: NO_HEARTBEAT_MS,
    });
    const first = collect();
    const unsubscribeFirst = hub.subscribe(first.listener);
    await flush();
    expect(hub.getSnapshot()).not.toBeNull();

    unsubscribeFirst();
    // Polling keeps warming the snapshot during the grace window.
    expect(pollDelays(clock)).toEqual([2_000, 30_000]);
    clock.advance(10_000);
    await flush();
    expect(hub.getSnapshot()).not.toBeNull();

    clock.advance(20_000);
    await flush();
    expect(hub.getSnapshot()).toBeNull();
    expect(hub.isLive()).toBe(false);
    expect(pollDelays(clock)).toEqual([]);
    const callsAfterIdle = loadMachines.mock.calls.length;
    clock.advance(10_000);
    await flush();
    expect(loadMachines.mock.calls.length).toBe(callsAfterIdle);

    const second = collect();
    const unsubscribeSecond = hub.subscribe(second.listener);
    await flush();
    expect(loadMachines.mock.calls.length).toBe(callsAfterIdle + 1);
    expect(second.machinesEvents()).toHaveLength(1);
    unsubscribeSecond();
    hub.stop();
  });

  it("stops delivering to unsubscribed listeners and never duplicates a poll in flight", async () => {
    const clock = createManualClock();
    let resolveLoad!: (machines: Machine[]) => void;
    const loadMachines = vi.fn(
      () =>
        new Promise<Machine[]>((resolve) => {
          resolveLoad = resolve;
        }),
    );
    const hub = createMachinesLiveHub({
      loadMachines,
      clock,
      heartbeatIntervalMs: NO_HEARTBEAT_MS,
    });
    const first = collect();
    const second = collect();
    const unsubscribeFirst = hub.subscribe(first.listener);
    hub.subscribe(second.listener);

    // Single flight: nothing is scheduled while the first poll is unresolved.
    clock.advance(10_000);
    await flush();
    expect(loadMachines).toHaveBeenCalledTimes(1);

    unsubscribeFirst();
    resolveLoad([rawMachine()]);
    await flush();
    expect(first.machinesEvents()).toHaveLength(0);
    expect(second.machinesEvents()).toHaveLength(1);
    expect(pollDelays(clock)).toEqual([2_000]);
    hub.stop();
  });

  it("exposes one globalThis singleton shared by duplicated route bundles", () => {
    const hub = getMachinesLiveHub();
    expect(getMachinesLiveHub()).toBe(hub);
    hub.stop();
  });
});

