import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import type { RentalMachine } from "@/lib/rental-machines";

const MACHINES: RentalMachine[] = [
  {
    id: "pc-1",
    name: "PC ONE",
    cpu: "AMD Ryzen 7",
    gpu: "NVIDIA RTX 4070",
    ram: 32,
    ramBytes: 34_359_738_368,
    storage: "—",
    priceSatang: 4_000,
    status: "available",
    game: "",
  },
];

const h = vi.hoisted(() => {
  const state = {
    snapshot: null as unknown,
    live: false,
    listeners: new Set<(event: unknown) => void>(),
  };
  return {
    state,
    subscribe: vi.fn((listener: (event: unknown) => void) => {
      state.listeners.add(listener);
      return vi.fn(() => {
        state.listeners.delete(listener);
      });
    }),
    getSnapshot: vi.fn(() => state.snapshot as never),
    isLive: vi.fn(() => state.live),
    emit(event: unknown) {
      for (const listener of [...state.listeners]) listener(event);
    },
  };
});

vi.mock("@/lib/machines-live", () => ({
  getMachinesLiveHub: () => ({
    subscribe: h.subscribe,
    getSnapshot: h.getSnapshot,
    isLive: h.isLive,
  }),
}));

import * as streamRoute from "@/app/api/machines/stream/route";

const decoder = new TextDecoder();

function snapshot(version = "version-1") {
  return {
    machines: MACHINES,
    version,
    serverTimeUtc: "2026-11-22T12:00:00.000Z",
    updatedAtMs: Date.parse("2026-11-22T12:00:00.000Z"),
  };
}

function connect(path = "http://localhost/api/machines/stream") {
  const abort = new AbortController();
  const request = new Request(path, { signal: abort.signal });
  return { abort, request };
}

async function readFrame(reader: ReadableStreamDefaultReader<Uint8Array>) {
  const result = await reader.read();
  if (result.done) return null;
  const text = decoder.decode(result.value);
  const lines = text.split("\n");
  const event = lines.find((line) => line.startsWith("event: "))?.slice(7) ?? null;
  const rawData = lines.find((line) => line.startsWith("data: "))?.slice(6);
  return { text, event, data: rawData === undefined ? null : (JSON.parse(rawData) as unknown) };
}

describe("GET /api/machines/stream", () => {
  beforeEach(() => {
    h.state.snapshot = null;
    h.state.live = false;
    h.state.listeners.clear();
    h.subscribe.mockClear();
    h.getSnapshot.mockClear();
    h.isLive.mockClear();
  });

  afterEach(() => {
    h.state.listeners.clear();
  });

  it("returns a 200 SSE response with buffering-hostile headers and a retry hint", async () => {
    const { abort, request } = connect();
    const response = await streamRoute.GET(request);

    expect(response.status).toBe(200);
    expect(response.headers.get("content-type")).toBe("text/event-stream; charset=utf-8");
    expect(response.headers.get("cache-control")).toBe("no-store, no-transform");
    expect(response.headers.get("connection")).toBe("keep-alive");
    expect(response.headers.get("x-accel-buffering")).toBe("no");

    const reader = response.body!.getReader();
    const first = await readFrame(reader);
    expect(first?.text).toBe("retry: 3000\n\n");
    expect(first?.event).toBeNull();

    // No snapshot yet: the first state frame reports the hub as offline.
    const initialStatus = await readFrame(reader);
    expect(initialStatus?.event).toBe("status");
    expect(initialStatus?.data).toMatchObject({ live: false });
    expect(h.state.listeners.size).toBe(1);

    abort.abort();
    await reader.cancel().catch(() => {});
    expect(h.state.listeners.size).toBe(0);
  });

  it("replays the hub snapshot after subscribing so no update is missed", async () => {
    h.state.snapshot = snapshot("version-abc");
    h.state.live = true;
    const { abort, request } = connect();
    const response = await streamRoute.GET(request);

    const reader = response.body!.getReader();
    expect((await readFrame(reader))?.text).toContain("retry: 3000");
    const machines = await readFrame(reader);
    expect(machines?.event).toBe("machines");
    expect(machines?.data).toMatchObject({
      version: "version-abc",
      machines: [{ id: "pc-1", priceSatang: 4_000 }],
    });

    // Subscribe happened before the snapshot was read.
    expect(h.subscribe.mock.invocationCallOrder[0]).toBeLessThan(
      h.getSnapshot.mock.invocationCallOrder[0],
    );

    abort.abort();
    await reader.cancel().catch(() => {});
  });

  it("tags a replayed snapshot as offline when the hub is not live", async () => {
    h.state.snapshot = snapshot("version-stale");
    h.state.live = false;
    const { abort, request } = connect();
    const response = await streamRoute.GET(request);

    const reader = response.body!.getReader();
    await readFrame(reader); // retry
    const status = await readFrame(reader);
    expect(status?.event).toBe("status");
    expect(status?.data).toMatchObject({ live: false });
    const machines = await readFrame(reader);
    expect(machines?.event).toBe("machines");
    expect(machines?.data).toMatchObject({ version: "version-stale" });

    abort.abort();
    await reader.cancel().catch(() => {});
  });

  it("forwards live machines, status and heartbeat events", async () => {
    h.state.snapshot = snapshot();
    h.state.live = true;
    const { abort, request } = connect();
    const response = await streamRoute.GET(request);
    const reader = response.body!.getReader();
    await readFrame(reader); // retry
    await readFrame(reader); // initial machines snapshot

    h.emit({ type: "machines", snapshot: snapshot("version-2") });
    const machines = await readFrame(reader);
    expect(machines?.event).toBe("machines");
    expect(machines?.data).toMatchObject({ version: "version-2" });

    h.emit({ type: "status", live: false, serverTimeUtc: "2026-11-22T12:00:05.000Z" });
    const status = await readFrame(reader);
    expect(status?.event).toBe("status");
    expect(status?.data).toEqual({ live: false, serverTimeUtc: "2026-11-22T12:00:05.000Z" });

    h.emit({ type: "heartbeat", serverTimeUtc: "2026-11-22T12:00:15.000Z" });
    const heartbeat = await readFrame(reader);
    expect(heartbeat?.event).toBe("heartbeat");
    expect(heartbeat?.data).toEqual({ serverTimeUtc: "2026-11-22T12:00:15.000Z" });

    abort.abort();
    await reader.cancel().catch(() => {});
  });

  it("releases the hub subscription when the request aborts", async () => {
    const { abort, request } = connect();
    const response = await streamRoute.GET(request);
    const reader = response.body!.getReader();
    await readFrame(reader);
    expect(h.state.listeners.size).toBe(1);

    abort.abort();
    expect(h.state.listeners.size).toBe(0);
    // Cleanup is idempotent; already-queued frames drain, then the stream ends.
    let result = await reader.read();
    while (!result.done) result = await reader.read();
    expect(result.done).toBe(true);
  });

  it("releases the hub subscription when the consumer cancels the stream", async () => {
    const { request } = connect();
    const response = await streamRoute.GET(request);
    const reader = response.body!.getReader();
    await readFrame(reader);
    expect(h.state.listeners.size).toBe(1);

    await reader.cancel();
    expect(h.state.listeners.size).toBe(0);
  });

  it("always answers 200 and reports offline when the hub itself fails", async () => {
    h.subscribe.mockImplementationOnce(() => {
      throw new Error("hub unavailable");
    });
    const { request } = connect();
    const response = await streamRoute.GET(request);

    expect(response.status).toBe(200);
    const reader = response.body!.getReader();
    expect((await readFrame(reader))?.text).toContain("retry: 3000");
    const status = await readFrame(reader);
    expect(status?.event).toBe("status");
    expect(status?.data).toMatchObject({ live: false });
    // The broken stream is closed so EventSource reconnects immediately.
    expect(await reader.read()).toMatchObject({ done: true });
  });

  it("honours a clamped ?retry= override, proving request.url is request data", async () => {
    const cases: [string, string][] = [
      ["?retry=1000", "retry: 1000\n\n"],
      ["?retry=5000", "retry: 5000\n\n"],
      ["?retry=999999", "retry: 15000\n\n"],
      ["?retry=nope", "retry: 3000\n\n"],
    ];
    for (const [query, expected] of cases) {
      const { abort, request } = connect(`http://localhost/api/machines/stream${query}`);
      const response = await streamRoute.GET(request);
      const reader = response.body!.getReader();
      expect((await readFrame(reader))?.text).toBe(expected);
      abort.abort();
      await reader.cancel().catch(() => {});
    }
  });

  it("does not export the removed route segment configs", () => {
    const route = streamRoute as unknown as Record<string, unknown>;
    expect(route.dynamic).toBeUndefined();
    expect(route.revalidate).toBeUndefined();
  });
});
