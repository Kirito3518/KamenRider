import {
  getMachinesLiveHub,
  type MachinesLiveEvent,
} from "@/lib/machines-live";

/**
 * Public Server-Sent Events stream of the customer machine list.
 *
 * Every connected browser receives the latest public snapshot pushed by the
 * process-wide hub (`@/lib/machines-live`), which polls the Controller once for
 * all listeners. Event contract:
 *
 * - `retry: 3000` — browser reconnect delay (overridable with `?retry=`).
 * - `event: machines` — `{ machines, version, serverTimeUtc, updatedAtMs }`.
 * - `event: status` — `{ live, serverTimeUtc }` when the Controller goes
 *   offline/recovered.
 * - `event: heartbeat` — `{ serverTimeUtc }` every 15s while connected.
 *
 * The handler intentionally reads `request.url` and binds `request.signal`:
 * under Cache Components those request-data accesses keep this route at request
 * time, so it must NOT export `dynamic` or `revalidate` (they are removed).
 */
export async function GET(request: Request) {
  // Request-data access #1: reading the URL opts this handler out of
  // prerendering (the retry hint is a real, per-request value).
  const retryMs = retryDelayMs(request.url);
  // Request-data access #2: the abort signal closes the stream when the client
  // disconnects, so the hub subscription and timers are always released.
  const { signal } = request;

  const encoder = new TextEncoder();
  let closed = false;
  let unsubscribe: (() => void) | null = null;
  let activeController: ReadableStreamDefaultController<Uint8Array> | null = null;

  function finish(closeStream: boolean) {
    if (closed) return;
    closed = true;
    signal.removeEventListener("abort", onAbort);
    const release = unsubscribe;
    unsubscribe = null;
    try {
      release?.();
    } catch {
      // Hub teardown must never throw back into the HTTP response.
    }
    if (!closeStream) return;
    const controller = activeController;
    activeController = null;
    try {
      controller?.close();
    } catch {
      // The consumer may have cancelled the stream already.
    }
  }

  function onAbort() {
    finish(true);
  }

  const body = new ReadableStream<Uint8Array>({
    start(controller) {
      activeController = controller;

      const send = (frame: string) => {
        const target = activeController;
        if (closed || target === null) return;
        try {
          target.enqueue(encoder.encode(frame));
        } catch {
          finish(false);
        }
      };
      const sendEvent = (event: string, data: unknown) => {
        send(`event: ${event}\ndata: ${JSON.stringify(data)}\n\n`);
      };

      // `retry:` must be the first thing the browser sees.
      send(`retry: ${retryMs}\n\n`);

      try {
        const hub = getMachinesLiveHub();
        // Subscribe before reading the snapshot so no update can be missed
        // between the initial frame and the live stream.
        unsubscribe = hub.subscribe((event: MachinesLiveEvent) => {
          switch (event.type) {
            case "machines":
              sendEvent("machines", event.snapshot);
              break;
            case "status":
              sendEvent("status", {
                live: event.live,
                serverTimeUtc: event.serverTimeUtc,
              });
              break;
            case "heartbeat":
              sendEvent("heartbeat", { serverTimeUtc: event.serverTimeUtc });
              break;
          }
        });
        const snapshot = hub.getSnapshot();
        // Replay freshness only when the hub is currently offline: a live
        // snapshot speaks for itself, a stale one must not look fresh.
        if (!hub.isLive()) {
          sendEvent("status", {
            live: false,
            serverTimeUtc: new Date().toISOString(),
          });
        }
        if (snapshot) sendEvent("machines", snapshot);
      } catch {
        // Always answer 200: report the offline state, then let the browser's
        // EventSource reconnect (the client also falls back to polling).
        sendEvent("status", { live: false, serverTimeUtc: new Date().toISOString() });
        finish(true);
        return;
      }

      if (signal.aborted) finish(true);
      else signal.addEventListener("abort", onAbort, { once: true });
    },
    cancel() {
      finish(false);
    },
  });

  return new Response(body, {
    status: 200,
    headers: {
      "Content-Type": "text/event-stream; charset=utf-8",
      "Cache-Control": "no-store, no-transform",
      Connection: "keep-alive",
      "X-Accel-Buffering": "no",
    },
  });
}

const DEFAULT_RETRY_MS = 3_000;
const MIN_RETRY_MS = 1_000;
const MAX_RETRY_MS = 15_000;

/** `?retry=` lets a client tune its reconnect delay; clamped so it stays sane. */
function retryDelayMs(requestUrl: string) {
  try {
    const raw = new URL(requestUrl).searchParams.get("retry");
    if (raw === null) return DEFAULT_RETRY_MS;
    const parsed = Number.parseInt(raw, 10);
    if (!Number.isFinite(parsed)) return DEFAULT_RETRY_MS;
    return Math.min(MAX_RETRY_MS, Math.max(MIN_RETRY_MS, parsed));
  } catch {
    return DEFAULT_RETRY_MS;
  }
}
