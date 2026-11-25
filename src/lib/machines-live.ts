import "server-only";
import { createHash } from "node:crypto";
import { controllerRequest } from "@/lib/controller-client";
import type { Machine } from "@/lib/machine-contracts";
import { toPublicMachines } from "@/lib/public-machines";
import type { RentalMachine } from "@/lib/rental-machines";

/** How often the hub polls the Controller for the raw machine list. */
export const MACHINES_LIVE_POLL_INTERVAL_MS = 2_000;
/** Keepalive cadence so SSE clients can detect a dead stream. */
export const MACHINES_LIVE_HEARTBEAT_INTERVAL_MS = 15_000;
/** Keep polling for this long after the last subscriber leaves, then idle stop. */
export const MACHINES_LIVE_IDLE_STOP_MS = 30_000;
/** Delay between retries after a Controller failure: 1s, 2s, 4s, 8s, then 15s. */
export const MACHINES_LIVE_BACKOFF_MS = [1_000, 2_000, 4_000, 8_000, 15_000] as const;

const VERSION_HASH_LENGTH = 32;

export type MachinesLiveTimer = ReturnType<typeof setTimeout> | number;

export type MachinesLiveSnapshot = {
  /** Public machine list: exactly the array served by `GET /api/machines`. */
  machines: RentalMachine[];
  /** sha256 hex (truncated) of `JSON.stringify(machines)`; changes when visible data changes. */
  version: string;
  serverTimeUtc: string;
  updatedAtMs: number;
};

export type MachinesLiveEvent =
  | { type: "machines"; snapshot: MachinesLiveSnapshot }
  | { type: "status"; live: boolean; serverTimeUtc: string }
  | { type: "heartbeat"; serverTimeUtc: string };

export type MachinesLiveListener = (event: MachinesLiveEvent) => void;

/** Injectable timers/clock so the hub can be driven deterministically in tests. */
export type MachinesLiveClock = {
  now: () => number;
  setTimer: (callback: () => void, delayMs: number) => MachinesLiveTimer;
  clearTimer: (timer: MachinesLiveTimer) => void;
};

export type MachinesLiveHubOptions = {
  loadMachines?: () => Promise<Machine[]>;
  versionOf?: (machines: RentalMachine[]) => string;
  clock?: Partial<MachinesLiveClock>;
  pollIntervalMs?: number;
  heartbeatIntervalMs?: number;
  idleStopMs?: number;
  backoffMs?: readonly number[];
};

export type MachinesLiveHub = {
  /**
   * Register a listener. The first subscriber starts polling and heartbeats;
   * the returned disposer follows a last-subscriber idle stop after
   * `idleStopMs`. A synchronous `"machines"` replay is intentionally *not*
   * sent here: callers read `getSnapshot()` once to build the initial frame.
   */
  subscribe: (listener: MachinesLiveListener) => () => void;
  getSnapshot: () => MachinesLiveSnapshot | null;
  isLive: () => boolean;
  /** Permanently stop the hub (tests/shutdown); ignores later subscribe calls. */
  stop: () => void;
};

function defaultVersionOf(machines: RentalMachine[]) {
  return createHash("sha256")
    .update(JSON.stringify(machines))
    .digest("hex")
    .slice(0, VERSION_HASH_LENGTH);
}

function defaultLoadMachines() {
  return controllerRequest<Machine[]>("/internal/machines");
}

/**
 * Single-flight machine snapshot hub shared by every open SSE stream.
 *
 * One Controller poll feeds all subscribers (the route bundle may be instantiated
 * more than once, so the production singleton lives on `globalThis`). Polling
 * starts with the first subscriber, keeps the last good snapshot while the
 * Controller is failing (backoff 1s/2s/4s/8s/15s), resumes at the normal 2s
 * cadence after a success, and stops 30s after the last subscriber leaves.
 */
export function createMachinesLiveHub(
  options: MachinesLiveHubOptions = {},
): MachinesLiveHub {
  const loadMachines = options.loadMachines ?? defaultLoadMachines;
  const versionOf = options.versionOf ?? defaultVersionOf;
  const clock: MachinesLiveClock = {
    now: options.clock?.now ?? (() => Date.now()),
    setTimer:
      options.clock?.setTimer ??
      ((callback, delayMs) => setTimeout(callback, delayMs)),
    clearTimer:
      options.clock?.clearTimer ?? ((timer) => clearTimeout(timer)),
  };
  const pollIntervalMs =
    options.pollIntervalMs ?? MACHINES_LIVE_POLL_INTERVAL_MS;
  const heartbeatIntervalMs =
    options.heartbeatIntervalMs ?? MACHINES_LIVE_HEARTBEAT_INTERVAL_MS;
  const idleStopMs = options.idleStopMs ?? MACHINES_LIVE_IDLE_STOP_MS;
  const backoffMs =
    options.backoffMs && options.backoffMs.length > 0
      ? options.backoffMs
      : MACHINES_LIVE_BACKOFF_MS;

  const listeners = new Set<MachinesLiveListener>();
  let snapshot: MachinesLiveSnapshot | null = null;
  let live = false;
  let failures = 0;
  let pollTimer: MachinesLiveTimer | null = null;
  let heartbeatTimer: MachinesLiveTimer | null = null;
  let idleTimer: MachinesLiveTimer | null = null;
  let pollInFlight = false;
  /** True while nobody is watching and the idle-stop timer may fire. */
  let idle = true;
  let stopped = false;

  function serverTimeUtc() {
    return new Date(clock.now()).toISOString();
  }

  function emit(event: MachinesLiveEvent) {
    // Snapshot the subscriber list: a listener may unsubscribe while fanning out.
    for (const listener of [...listeners]) {
      try {
        listener(event);
      } catch {
        // A broken subscriber (e.g. a closed stream) must not starve the others.
      }
    }
  }

  function clearPollTimer() {
    if (pollTimer === null) return;
    clock.clearTimer(pollTimer);
    pollTimer = null;
  }

  function clearHeartbeatTimer() {
    if (heartbeatTimer === null) return;
    clock.clearTimer(heartbeatTimer);
    heartbeatTimer = null;
  }

  function clearIdleTimer() {
    if (idleTimer === null) return;
    clock.clearTimer(idleTimer);
    idleTimer = null;
  }

  function schedulePoll(delayMs: number) {
    clearPollTimer();
    if (stopped || idle) return;
    pollTimer = clock.setTimer(() => {
      pollTimer = null;
      void poll();
    }, delayMs);
  }

  function scheduleHeartbeat() {
    clearHeartbeatTimer();
    if (stopped) return;
    heartbeatTimer = clock.setTimer(() => {
      heartbeatTimer = null;
      if (stopped || idle) return;
      emit({ type: "heartbeat", serverTimeUtc: serverTimeUtc() });
      scheduleHeartbeat();
    }, heartbeatIntervalMs);
  }

  function stopActiveWork() {
    idle = true;
    clearPollTimer();
    clearHeartbeatTimer();
    clearIdleTimer();
    snapshot = null;
    live = false;
    failures = 0;
  }

  function scheduleIdleStop() {
    clearIdleTimer();
    idleTimer = clock.setTimer(() => {
      idleTimer = null;
      stopActiveWork();
    }, idleStopMs);
  }

  async function poll() {
    if (stopped || idle || pollInFlight) return;
    pollInFlight = true;
    try {
      const machines = toPublicMachines(await loadMachines());
      if (stopped || idle) return;
      const version = versionOf(machines);
      const changed = snapshot === null || snapshot.version !== version;
      const updatedAtMs = clock.now();
      snapshot = {
        machines,
        version,
        serverTimeUtc: new Date(updatedAtMs).toISOString(),
        updatedAtMs,
      };
      failures = 0;
      if (!live) {
        live = true;
        emit({ type: "status", live: true, serverTimeUtc: snapshot.serverTimeUtc });
      }
      if (changed) emit({ type: "machines", snapshot });
      schedulePoll(pollIntervalMs);
    } catch {
      if (stopped || idle) return;
      failures += 1;
      if (live) {
        live = false;
        emit({ type: "status", live: false, serverTimeUtc: serverTimeUtc() });
      }
      const delayMs = backoffMs[Math.min(failures - 1, backoffMs.length - 1)];
      schedulePoll(delayMs ?? pollIntervalMs);
    } finally {
      pollInFlight = false;
    }
  }

  function subscribe(listener: MachinesLiveListener) {
    if (stopped) return () => {};
    listeners.add(listener);
    clearIdleTimer();
    if (idle) {
      idle = false;
      // First watcher for a while: refresh immediately instead of waiting a tick.
      void poll();
    }
    if (listeners.size === 1) scheduleHeartbeat();
    let active = true;
    return () => {
      if (!active) return;
      active = false;
      if (!listeners.delete(listener)) return;
      if (listeners.size > 0) return;
      // Nobody is listening anymore: keep the snapshot warm for the grace window,
      // but stop emitting heartbeats right away.
      clearHeartbeatTimer();
      scheduleIdleStop();
    };
  }

  function getSnapshot() {
    return snapshot;
  }

  function isLive() {
    return live;
  }

  function stop() {
    if (stopped) return;
    stopped = true;
    listeners.clear();
    stopActiveWork();
  }

  return { subscribe, getSnapshot, isLive, stop };
}

type MachinesLiveGlobal = typeof globalThis & {
  __machinesLiveHub?: MachinesLiveHub;
};

/**
 * Production singleton. Route bundles can duplicate this module, so the shared
 * instance is stored on `globalThis` instead of a module-level variable.
 *
 * Single-Node assumption: the Controller URL, service credential and machine
 * list are cluster-wide, so every Node process polls its own hub and any of
 * them can serve any SSE client.
 */
export function getMachinesLiveHub(): MachinesLiveHub {
  const scope = globalThis as MachinesLiveGlobal;
  scope.__machinesLiveHub ??= createMachinesLiveHub();
  return scope.__machinesLiveHub;
}
