"use client";

import { useEffect, useState } from "react";
import type { RentalMachine } from "@/lib/rental-machines";

const HYDRATION_URL = "/api/machines";
const STREAM_URL = "/api/machines/stream";
const POLL_INTERVAL_MS = 3_000;
/** Treat the stream as stalled after this long without any event. */
const STALL_TIMEOUT_MS = 25_000;
const STALL_CHECK_INTERVAL_MS = 5_000;
const RECONNECT_BASE_MS = 3_000;
const RECONNECT_MAX_MS = 30_000;
const CONNECTION_ERROR_MESSAGE = "ติดต่อระบบเครื่องไม่ได้ กรุณาลองใหม่";

export type RentalMachinesState = {
  /** Last known customer-facing machine list; never blanked by a transient error. */
  machines: RentalMachine[];
  /** True until the first successful hydration (or stream/poll) response. */
  loading: boolean;
  /** True while the SSE stream is delivering fresh data; false in polling fallback. */
  live: boolean;
  /** Human-readable connection problem, kept while stale data is displayed. */
  error: string | null;
};

type MachinesEventPayload = {
  machines: RentalMachine[];
  version: string | null;
};

function isRecord(value: unknown): value is Record<string, unknown> {
  return typeof value === "object" && value !== null && !Array.isArray(value);
}

function isRentalMachine(value: unknown): value is RentalMachine {
  if (!isRecord(value)) return false;
  return (
    typeof value.id === "string" &&
    typeof value.name === "string" &&
    typeof value.status === "string" &&
    typeof value.priceSatang === "number"
  );
}

function isRentalMachineList(value: unknown): value is RentalMachine[] {
  return Array.isArray(value) && value.every(isRentalMachine);
}

function isAbortError(error: unknown) {
  return isRecord(error) && error.name === "AbortError";
}

function parseJson(data: unknown): unknown {
  if (typeof data !== "string") return null;
  try {
    return JSON.parse(data) as unknown;
  } catch {
    return null;
  }
}

function parseMachinesEvent(data: unknown): MachinesEventPayload | null {
  const parsed = parseJson(data);
  if (!isRecord(parsed) || !isRentalMachineList(parsed.machines)) return null;
  return {
    machines: parsed.machines,
    version: typeof parsed.version === "string" ? parsed.version : null,
  };
}

function parseStatusEvent(data: unknown): boolean | null {
  const parsed = parseJson(data);
  if (!isRecord(parsed) || typeof parsed.live !== "boolean") return null;
  return parsed.live;
}

/**
 * Live customer machine list for `/rent`.
 *
 * - Hydrates with `GET /api/machines` exactly like the pre-realtime page.
 * - Then follows the public SSE stream (`/api/machines/stream`), applying
 *   `machines` events only when their snapshot version changes.
 * - Falls back to 3s polling while the document is visible whenever
 *   `EventSource` is missing, the stream errors, or no event arrives for 25s,
 *   and keeps the previous list while an error is showing (stale-while-error).
 * - Reconnects the stream on focus / online / visibilitychange, with
 *   exponential backoff for automatic retries.
 * - Cleans up every timer, listener and EventSource on unmount.
 */
export function useRentalMachines(): RentalMachinesState {
  const [state, setState] = useState<RentalMachinesState>({
    machines: [],
    loading: true,
    live: false,
    error: null,
  });

  useEffect(() => {
    let disposed = false;
    let source: EventSource | null = null;
    let pollTimer: ReturnType<typeof setInterval> | null = null;
    let stallTimer: ReturnType<typeof setInterval> | null = null;
    let reconnectTimer: ReturnType<typeof setTimeout> | null = null;
    let hydrateAbort: AbortController | null = null;
    let pollAbort: AbortController | null = null;
    let pollInFlight = false;
    let reconnectAttempt = 0;
    let lastEventAt = 0;
    let appliedVersion: string | null = null;
    /** Freshness reported by the hub; a replayed snapshot keeps this value. */
    let streamLive = true;
    let hasData = false;

    function applyMachines(
      machines: RentalMachine[],
      options: { version?: string | null; live?: boolean },
    ) {
      if (disposed) return;
      hasData = true;
      if (options.version !== undefined) appliedVersion = options.version;
      setState((previous) => ({
        machines,
        loading: false,
        live: options.live ?? previous.live,
        error: null,
      }));
    }

    function applyError(message: string, options: { live?: boolean } = {}) {
      if (disposed) return;
      setState((previous) => ({
        ...previous,
        loading: false,
        live: options.live ?? previous.live,
        error: message,
      }));
    }

    // ---------------------------------------------------------------- hydrate
    async function hydrate() {
      const abort = new AbortController();
      hydrateAbort = abort;
      try {
        const response = await fetch(HYDRATION_URL, {
          cache: "no-store",
          signal: abort.signal,
        });
        if (!response.ok) throw new Error("Machine list hydration failed");
        const payload: unknown = await response.json();
        if (disposed || abort.signal.aborted || hasData) return;
        if (!isRentalMachineList(payload)) throw new Error("Invalid machine list");
        applyMachines(payload, {});
      } catch (error) {
        if (disposed || abort.signal.aborted || isAbortError(error) || hasData) return;
        applyError(CONNECTION_ERROR_MESSAGE);
      }
    }

    // ---------------------------------------------------------------- polling
    function stopPolling() {
      if (pollTimer !== null) {
        clearInterval(pollTimer);
        pollTimer = null;
      }
      pollAbort?.abort();
      pollAbort = null;
      pollInFlight = false;
    }

    async function pollOnce() {
      if (disposed || pollInFlight) return;
      if (typeof document !== "undefined" && document.visibilityState !== "visible") return;
      pollInFlight = true;
      const abort = new AbortController();
      pollAbort = abort;
      try {
        const response = await fetch(HYDRATION_URL, {
          cache: "no-store",
          signal: abort.signal,
        });
        if (!response.ok) throw new Error("Machine list poll failed");
        const payload: unknown = await response.json();
        if (disposed || abort.signal.aborted) return;
        if (!isRentalMachineList(payload)) throw new Error("Invalid machine list");
        applyMachines(payload, { live: false });
      } catch (error) {
        if (disposed || abort.signal.aborted || isAbortError(error)) return;
        applyError(CONNECTION_ERROR_MESSAGE, { live: false });
      } finally {
        if (pollAbort === abort) pollAbort = null;
        pollInFlight = false;
      }
    }

    function startPolling() {
      stopPolling();
      pollTimer = setInterval(() => {
        void pollOnce();
      }, POLL_INTERVAL_MS);
    }

    // ------------------------------------------------------------ event stream
    function clearReconnectTimer() {
      if (reconnectTimer === null) return;
      clearTimeout(reconnectTimer);
      reconnectTimer = null;
    }

    function stopStream() {
      if (source === null) return;
      source.close();
      source = null;
      if (stallTimer !== null) {
        clearInterval(stallTimer);
        stallTimer = null;
      }
    }

    function scheduleReconnect() {
      if (disposed || reconnectTimer !== null) return;
      const delay = Math.min(
        RECONNECT_MAX_MS,
        RECONNECT_BASE_MS * 2 ** Math.min(reconnectAttempt, 3),
      );
      reconnectAttempt += 1;
      reconnectTimer = setTimeout(() => {
        reconnectTimer = null;
        connectStream();
      }, delay);
    }

    function degradeToPolling() {
      stopStream();
      streamLive = false;
      startPolling();
      setState((previous) =>
        previous.live ? { ...previous, live: false } : previous,
      );
      scheduleReconnect();
    }

    function connectStream() {
      if (disposed || source !== null) return;
      if (typeof EventSource === "undefined") {
        // No SSE support at all: poll as long as the tab is visible.
        startPolling();
        return;
      }
      let stream: EventSource;
      try {
        stream = new EventSource(STREAM_URL);
      } catch {
        degradeToPolling();
        return;
      }
      source = stream;
      lastEventAt = Date.now();
      const isCurrent = () => !disposed && source === stream;

      stream.addEventListener("machines", (event) => {
        if (!isCurrent()) return;
        lastEventAt = Date.now();
        reconnectAttempt = 0;
        const payload = parseMachinesEvent((event as MessageEvent).data);
        if (payload === null) return;
        // Any valid frame proves the stream is carrying live data again.
        stopPolling();
        // Version ordering guard: a reconnect replays the last snapshot. It is
        // still realtime data, so refresh `live` without touching the list.
        if (payload.version !== null && payload.version === appliedVersion) {
          setState((previous) => ({
            ...previous,
            live: streamLive,
            loading: false,
            error: streamLive ? null : previous.error,
          }));
          return;
        }
        applyMachines(payload.machines, {
          version: payload.version,
          live: streamLive,
        });
      });

      stream.addEventListener("status", (event) => {
        if (!isCurrent()) return;
        lastEventAt = Date.now();
        const live = parseStatusEvent((event as MessageEvent).data);
        if (live === null) return;
        streamLive = live;
        if (live) {
          reconnectAttempt = 0;
          stopPolling();
        }
        setState((previous) => ({
          ...previous,
          live,
          loading: false,
          error: live ? null : previous.error,
        }));
      });

      stream.addEventListener("heartbeat", () => {
        if (!isCurrent()) return;
        lastEventAt = Date.now();
      });

      stream.addEventListener("open", () => {
        if (!isCurrent()) return;
        // The stream is healthy again: stop the fallback poller and let the
        // server's initial status/machines frames restore `live`.
        lastEventAt = Date.now();
        reconnectAttempt = 0;
        streamLive = true;
        stopPolling();
      });

      stream.addEventListener("error", () => {
        if (!isCurrent()) return;
        degradeToPolling();
      });

      // Browsers throttle background timers, so this guard mainly detects a
      // silently dead stream while the customer is looking at the page.
      stallTimer = setInterval(() => {
        if (!isCurrent()) return;
        if (Date.now() - lastEventAt > STALL_TIMEOUT_MS) degradeToPolling();
      }, STALL_CHECK_INTERVAL_MS);
    }

    function handleWake() {
      if (disposed) return;
      if (source !== null) {
        // Still connected: if the stream went silent while the tab was in the
        // background, fall back and reconnect instead of trusting it.
        if (Date.now() - lastEventAt > STALL_TIMEOUT_MS) {
          degradeToPolling();
        } else {
          lastEventAt = Date.now();
        }
        return;
      }
      clearReconnectTimer();
      reconnectAttempt = 0;
      connectStream();
    }

    function handleVisibilityChange() {
      if (typeof document !== "undefined" && document.visibilityState === "visible") {
        handleWake();
      }
    }

    void hydrate();
    connectStream();
    window.addEventListener("focus", handleWake);
    window.addEventListener("online", handleWake);
    if (typeof document !== "undefined") {
      document.addEventListener("visibilitychange", handleVisibilityChange);
    }

    return () => {
      disposed = true;
      hydrateAbort?.abort();
      hydrateAbort = null;
      stopPolling();
      stopStream();
      clearReconnectTimer();
      window.removeEventListener("focus", handleWake);
      window.removeEventListener("online", handleWake);
      if (typeof document !== "undefined") {
        document.removeEventListener("visibilitychange", handleVisibilityChange);
      }
    };
  }, []);

  return state;
}
